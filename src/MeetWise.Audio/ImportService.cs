using System.ComponentModel;
using System.Threading.Channels;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.Audio;

public enum JobStatus { Queued, Working, Done, Failed }

/// <summary>One line of the Library's import list. Property changes are raised from the worker thread; the UI marshals them.</summary>
public sealed class ImportJob : INotifyPropertyChanged
{
    private JobStatus _status;
    private double _progress;
    private string _message = "";
    private long? _itemId;

    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsYouTube { get; init; }
    public JobStatus Status { get => _status; internal set => Set(ref _status, value); }
    public double Progress { get => _progress; internal set => Set(ref _progress, value); }
    public string Message { get => _message; internal set => Set(ref _message, value); }
    public long? ItemId { get => _itemId; internal set => Set(ref _itemId, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal TaskCompletionSource<ImportJob> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ImportOptions Options { get; init; } = new();
    internal string OutputFolder { get; init; } = "";
}

/// <summary>
/// Runs imports one at a time in the background (Whisper holds the GPU, so parallel jobs would only compete): audio and video files, YouTube links
/// and the watched folder all go through the same queue. Finished items appear in the library with their subtitles; failures keep their reason.
/// </summary>
public sealed class ImportService : IDisposable
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts", ".3gp" };

    private readonly MeetingDatabase _db;
    private readonly Func<Task<MediaImporter>> _media;
    private readonly Func<Task<YouTubeImporter>>? _youtube;
    private readonly Action? _onIdle;
    private readonly Channel<ImportJob> _queue = Channel.CreateUnbounded<ImportJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<ImportJob> _jobs = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private int _pending;

    public IReadOnlyList<ImportJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }
    public event Action<ImportJob>? JobAdded;
    public event Action<ImportJob>? JobChanged;
    public event Action<long>? ItemAdded;

    /// <param name="mediaImporter">Builds the importer (models are loaded lazily on the first job).</param>
    /// <param name="onIdle">Called when the queue is empty again, so the caller can release the model and its GPU memory.</param>
    public ImportService(MeetingDatabase db, Func<Task<MediaImporter>> mediaImporter, Func<Task<YouTubeImporter>>? youtubeImporter = null, Action? onIdle = null)
    {
        _db = db;
        _media = mediaImporter;
        _youtube = youtubeImporter;
        _onIdle = onIdle;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>The production setup: Whisper chosen by hardware tier, FFmpeg and yt-dlp downloaded on first use.</summary>
    public static ImportService Create(MeetingDatabase db, HttpClient http, HardwareProfile hw, Func<IReadOnlyList<string>> vocabulary, Action? onIdle = null)
    {
        WhisperTranscriber? whisper = null;
        string? ffmpeg = null;
        async Task<MediaImporter> Media()
        {
            ffmpeg ??= await FFmpegPack.EnsureAsync(http);
            if (whisper is null)
            {
                var manager = new ModelManager(http);
                var model = ModelCatalog.WhisperFor(hw);
                await manager.EnsureAsync(model);
                CudaRuntimePack.Activate();
                whisper = new WhisperTranscriber(manager.PathFor(model));
            }
            return MediaImporter.WithWhisper(ffmpeg, whisper, vocabulary());
        }
        async Task<YouTubeImporter> YouTube() => new(await YtDlpPack.EnsureAsync(http), await Media());
        return new ImportService(db, Media, YouTube, () => { whisper?.Dispose(); whisper = null; onIdle?.Invoke(); });
    }

    public ImportJob EnqueueFile(string path, ImportOptions options, string outputFolder)
        => Enqueue(new ImportJob { Name = Path.GetFileName(path), Source = path, Options = options, OutputFolder = outputFolder });

    public ImportJob EnqueueYouTube(string url, ImportOptions options, string outputFolder)
    {
        if (_youtube is null) throw new InvalidOperationException("YouTube import is not available.");
        var id = YouTubeUrl.ExtractId(url) ?? throw new ArgumentException("That is not a YouTube video link.", nameof(url));
        return Enqueue(new ImportJob { Name = "YouTube " + id, Source = url, IsYouTube = true, Options = options, OutputFolder = outputFolder });
    }

    /// <summary>For the watched folder: completes when the file has been transcribed, throws if it failed (so the file goes to Errors).</summary>
    public async Task ImportForWatchAsync(string file, string outputFolder, ImportOptions options)
    {
        var job = EnqueueFile(file, options, outputFolder);
        var done = await job.Completion.Task;
        if (done.Status == JobStatus.Failed) throw new InvalidOperationException(done.Message);
    }

    private ImportJob Enqueue(ImportJob job)
    {
        job.Status = JobStatus.Queued;
        lock (_gate) _jobs.Add(job);
        Interlocked.Increment(ref _pending);
        JobAdded?.Invoke(job);
        _queue.Writer.TryWrite(job);
        return job;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                job.Status = JobStatus.Working;
                job.Message = "";
                JobChanged?.Invoke(job);
                try
                {
                    var progress = new Progress<double>(p => { job.Progress = p; JobChanged?.Invoke(job); });
                    job.ItemId = job.IsYouTube ? await RunYouTubeAsync(job, progress) : await RunFileAsync(job, progress);
                    job.Progress = 1;
                    job.Status = JobStatus.Done;
                    if (job.ItemId is { } id) ItemAdded?.Invoke(id);
                }
                catch (OperationCanceledException) { job.Status = JobStatus.Failed; job.Message = "Cancelled."; }
                catch (YouTubeException ex) { job.Status = JobStatus.Failed; job.Message = ex.Message; }
                catch (Exception ex)
                {
                    AppLog.Warn($"Import of {job.Name} failed: {ex.Message}");
                    job.Status = JobStatus.Failed;
                    job.Message = ex.Message;
                }
                JobChanged?.Invoke(job);
                job.Completion.TrySetResult(job);
                if (Interlocked.Decrement(ref _pending) == 0) _onIdle?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<long> RunFileAsync(ImportJob job, IProgress<double> progress)
    {
        var importer = await _media();
        var r = await importer.ImportAsync(job.Source, job.OutputFolder, job.Options, progress, _cts.Token);
        bool video = VideoExtensions.Contains(Path.GetExtension(job.Source), StringComparer.OrdinalIgnoreCase);
        var id = _db.AddMeeting(Path.GetFileNameWithoutExtension(job.Source), DateTime.UtcNow, audioPath: video ? null : job.Source,
            videoPath: video ? job.Source : null, kind: "import", source: job.Source);
        Save(id, r.Cues);
        if (!r.TrackMatchedLanguage && job.Options.Language != "auto") job.Message = $"No '{job.Options.Language}' audio track; used the first track.";
        return id;
    }

    private async Task<long> RunYouTubeAsync(ImportJob job, IProgress<double> progress)
    {
        var yt = await _youtube!();
        var r = await yt.ImportAsync(job.Source, job.OutputFolder, job.Options, progress, _cts.Token);
        var id = _db.AddMeeting(r.Info.Title, DateTime.UtcNow, kind: "youtube", source: job.Source);
        Save(id, r.Cues);
        job.Message = r.Notice ?? (r.Source == TranscriptSource.OfficialCaptions ? "Official captions" : r.Source == TranscriptSource.AutoCaptions ? "Auto-generated captions" : "");
        return id;
    }

    private void Save(long itemId, IReadOnlyList<SubtitleCue> cues)
    {
        _db.ReplaceSegments(itemId, cues.Select(c => (c.Speaker.Length > 0 ? c.Speaker : "Speaker", c.Start, c.End, c.Text, c.Language)));
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
