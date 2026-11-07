using System.Diagnostics;
using MeetWise.Core;
using MeetWise.Core.Hardware;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using MeetWise.Storage;
using NAudio.Wave;

namespace MeetWise.Audio;

public enum CaptureMode { VideoAudio, AudioOnly, SubtitlesOnly }

public sealed record RecordingOptions(
    CaptureMode Mode, MonitorInfo? Monitor, QualityPreset Quality, bool Microphone, bool SystemAudio, string MicrophoneId,
    string SubtitleLanguage, bool TranslateToEnglish, bool WriteSrt, string OutputFolder,
    IReadOnlyList<string>? Vocabulary = null, string? Title = null);

public sealed record RecordingResult(long ItemId, string Title, string? VideoPath, string? AudioPath, string? SrtPath, string TxtPath,
    IReadOnlyList<SubtitleCue> Cues, TimeSpan Duration, string? Notice);

/// <summary>
/// One recording from start to finish: screen and audio are captured, subtitles appear live and are saved as they arrive (so a crash loses at most
/// the last second), and stopping produces the MP4 with both audio channels plus .srt and .txt files whose times match the video.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly MeetingDatabase _db;
    private readonly HttpClient _http;
    private readonly HardwareProfile _hardware;
    private readonly object _gate = new();
    private readonly List<SubtitleCue> _cues = new();
    private DualChannelRecorder? _recorder;
    private VideoRecorder? _video;
    private LiveSubtitler? _subtitler;
    private EnglishPunctuator? _punctuator;
    private RecordingOptions? _options;
    private Stopwatch? _clock;
    private Timer? _watchdog;
    private string? _ffmpeg, _baseName, _mkv;
    private long _itemId;
    private int _stopping;
    private volatile float _micPeak, _sysPeak;

    /// <summary>Free space lookup; replaceable for tests.</summary>
    public Func<string, long> FreeBytes { get; set; } = CaptureGuards.FreeBytes;
    public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long opening the audio devices may take before the recording is abandoned with a message.</summary>
    public TimeSpan AudioStartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Opens the capture devices; replaceable for tests (a device that never answers).</summary>
    internal Action<DualChannelRecorder, RecordingOptions, string> OpenAudio { get; set; } =
        (rec, o, baseName) => rec.Start(o.OutputFolder, baseName, o.MicrophoneId, o.Microphone, o.SystemAudio);

    /// <summary>Applied to every finished subtitle (the custom vocabulary corrector).</summary>
    public Func<string, string>? TextFilter { get; set; }

    public bool IsRecording { get; private set; }
    public long ItemId => _itemId;
    public TimeSpan Elapsed => _clock?.Elapsed ?? TimeSpan.Zero;
    public IReadOnlyList<SubtitleCue> Cues { get { lock (_gate) return _cues.ToList(); } }

    /// <summary>Current peak level (0 to 1) of the microphone and the system audio, decaying; poll it from a UI timer.</summary>
    public (double Microphone, double System) Levels => (_micPeak, _sysPeak);

    public event Action? Started;
    public event Action<string>? Status;
    public event Action<SubtitleCue>? Cue;
    public event Action<string, string>? Partial;
    public event Action<string, bool>? Speaking;
    public event Action<RecordingResult>? Stopped;
    public event Action<string>? Failed;

    public RecordingSession(MeetingDatabase db, HttpClient http, HardwareProfile hardware)
    {
        _db = db;
        _http = http;
        _hardware = hardware;
    }

    /// <summary>The models and tools this recording needs, so the UI can ask permission (GPU pack) and show progress before starting.</summary>
    public static IReadOnlyList<ModelEntry> RequiredModels(RecordingOptions o, HardwareProfile hw)
    {
        var list = new List<ModelEntry>();
        if (o.SubtitleLanguage == "en" && !o.TranslateToEnglish) { list.Add(ModelCatalog.StreamingEnglish); list.Add(ModelCatalog.PunctuationEnglish); }
        else { list.Add(ModelCatalog.SileroVad); list.Add(ModelCatalog.WhisperFor(hw)); }
        return list;
    }

    /// <summary>Seconds the subtitles must be shifted to match the video: the audio starts a little after the screen capture does.</summary>
    public static double SubtitleOffset(DateTime audioStartUtc, DateTime? videoStartUtc)
        => videoStartUtc is { } v ? Math.Max(0, (audioStartUtc - v).TotalSeconds) : 0;

    public async Task StartAsync(RecordingOptions options, CancellationToken ct = default)
    {
        if (IsRecording) throw new InvalidOperationException("A recording is already running.");
        if (!options.Microphone && !options.SystemAudio) throw new InvalidOperationException(Loc("Choose at least one audio source."));
        Directory.CreateDirectory(options.OutputFolder);
        if (CaptureGuards.CheckCanStart(options.OutputFolder, FreeBytes) is { } diskMessage) throw new InvalidOperationException(diskMessage);

        _options = options;
        lock (_gate) _cues.Clear();
        _stopping = 0;
        _micPeak = _sysPeak = 0;

        // 1. models (downloads show progress; nothing is recorded until everything is ready)
        Status?.Invoke("Preparing…");
        var manager = new ModelManager(_http);
        foreach (var m in RequiredModels(options, _hardware))
            await manager.EnsureAsync(m, new Progress<double>(p => Status?.Invoke($"Downloading {m.Id}… {p:P0}")), ct);
        CudaRuntimePack.Activate();

        // 2. subtitler
        Func<string, string>? punctuate = null;
        if (options.SubtitleLanguage == "en" && !options.TranslateToEnglish)
        {
            _punctuator = new EnglishPunctuator(manager.PathFor(ModelCatalog.PunctuationEnglish));
            punctuate = _punctuator.Restore;
        }
        bool streaming = options.SubtitleLanguage == "en" && !options.TranslateToEnglish;
        var whisperModel = streaming ? null : manager.PathFor(ModelCatalog.WhisperFor(_hardware));
        var vad = manager.PathFor(ModelCatalog.SileroVad);
        if (streaming && !File.Exists(vad)) vad = ""; // the English streaming path needs no voice-activity model
        _subtitler = await Task.Run(() => new LiveSubtitler(new LiveSubtitlerOptions(options.SubtitleLanguage, options.TranslateToEnglish, vad, whisperModel,
            streaming ? manager.PathFor(ModelCatalog.StreamingEnglish) : null, punctuate, options.Vocabulary, Math.Max(2, _hardware.CpuThreads / 4))), ct);
        _subtitler.Cue += HandleCue;
        _subtitler.Partial += (ch, text) => Partial?.Invoke(ch, text);
        _subtitler.Speaking += (ch, on) => Speaking?.Invoke(ch, on);

        // 3. video (before audio so the audio can be delayed to match; both start within a fraction of a second)
        _baseName = $"recording-{DateTime.Now:yyyyMMdd-HHmmss}";
        _mkv = null;
        if (options.Mode == CaptureMode.VideoAudio)
        {
            Status?.Invoke("Preparing video…");
            _ffmpeg = await FFmpegPack.EnsureAsync(_http, new Progress<double>(p => Status?.Invoke($"Downloading FFmpeg… {p:P0}")), ct: ct);
            var encoders = await VideoRecorder.ProbeEncodersAsync(_ffmpeg);
            _mkv = Path.Combine(options.OutputFolder, _baseName + ".video.mkv");
            _video = new VideoRecorder(_ffmpeg);
            _video.Start(_mkv, encoders[0], monitor: options.Monitor, quality: options.Quality);
        }

        // 4. audio. Opening a WASAPI device can block for a long time (or for ever) when a driver is busy or wedged, so it runs off the UI thread
        // with a time limit: the window stays alive and the user gets a clear message instead of a frozen app.
        var recorder = _recorder = new DualChannelRecorder();
        var opening = Task.Run(() => OpenAudio(recorder, options, _baseName!));
        try
        {
            if (await Task.WhenAny(opening, Task.Delay(AudioStartTimeout, ct)) != opening)
            {
                // abandon the stuck call; if it ever returns, release the device it grabbed
                _ = opening.ContinueWith(_ => { try { recorder.Dispose(); } catch { } }, TaskScheduler.Default);
                throw new TimeoutException(Loc($"The audio device did not start within {AudioStartTimeout.TotalSeconds:0} seconds. It may be busy or not responding: close other apps that use it, unplug and plug it in again, or choose another microphone."));
            }
            await opening;
        }
        catch
        {
            _recorder = null;
            _video?.Dispose(); // never leave a screen recording running when the audio device failed
            _video = null;
            _subtitler.Dispose();
            _subtitler = null;
            throw;
        }
        if (_recorder.MicFormat is { } mf && options.Microphone)
            _subtitler.AddChannel("Me", mf.SampleRate, mf.Channels, mf.Encoding == WaveFormatEncoding.IeeeFloat);
        if (_recorder.LoopbackFormat is { } lf && options.SystemAudio)
            _subtitler.AddChannel("Others", lf.SampleRate, lf.Channels, lf.Encoding == WaveFormatEncoding.IeeeFloat);
        _recorder.DataAvailable += OnAudio;

        // 5. bookkeeping
        var title = options.Title ?? $"Recording {DateTime.Now:yyyy-MM-dd HH:mm}";
        _itemId = _db.AddMeeting(title, DateTime.UtcNow, audioPath: _recorder.MicPath ?? _recorder.LoopbackPath, kind: "recording",
            source: options.Monitor?.DisplayName);
        _clock = Stopwatch.StartNew();
        CaptureGuards.KeepAwake(true);
        _watchdog = new Timer(_ => CheckDisk(), null, WatchdogInterval, WatchdogInterval);
        IsRecording = true;
        Status?.Invoke("Recording");
        Started?.Invoke();
    }

    private void OnAudio(string source, WaveFormat fmt, byte[] buffer, int count)
    {
        float peak = Peak(buffer, count, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
        if (source == "mic") _micPeak = Math.Max(peak, _micPeak * 0.85f); else _sysPeak = Math.Max(peak, _sysPeak * 0.85f);
        _subtitler?.Push(source == "mic" ? "Me" : "Others", buffer, count);
    }

    internal static float Peak(byte[] buffer, int count, bool isFloat32)
    {
        float peak = 0;
        if (isFloat32)
            for (int i = 0; i + 4 <= count; i += 16) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i))); // every 4th sample is plenty for a meter
        else
            for (int i = 0; i + 2 <= count; i += 8) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
        return Math.Min(1f, peak);
    }

    private void HandleCue(SubtitleCue cue)
    {
        var text = TextFilter?.Invoke(cue.Text) ?? cue.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        cue = cue with { Text = text };
        lock (_gate) _cues.Add(cue);
        if (_itemId != 0) _db.AddSegment(_itemId, cue.Speaker.Length > 0 ? cue.Speaker : "Speaker", cue.Start, cue.End, cue.Text, cue.Language);
        Cue?.Invoke(cue);
    }

    /// <summary>Adds a subtitle as if it had been recognised. Used by tests; the audio engine is not involved.</summary>
    internal void InjectCue(SubtitleCue cue) => HandleCue(cue);

    private void CheckDisk()
    {
        if (!IsRecording || _options is null) return;
        if (!CaptureGuards.ShouldStopForSpace(_options.OutputFolder, FreeBytes)) return;
        Failed?.Invoke("The disk is almost full, so the recording was stopped and saved.");
        _ = StopAsync();
    }

    public async Task<RecordingResult> StopAsync()
    {
        if (!IsRecording || Interlocked.Exchange(ref _stopping, 1) == 1) throw new InvalidOperationException("No recording is running.");
        var options = _options!;
        _watchdog?.Dispose();
        _watchdog = null;
        _recorder!.DataAvailable -= OnAudio;
        _recorder.Stop();
        var audioStart = _recorder.StartedUtc;
        var micWav = _recorder.MicPath;
        var loopWav = _recorder.LoopbackPath;
        var duration = _clock!.Elapsed;
        Status?.Invoke("Finishing the video and subtitles…");

        await Task.Run(() => _subtitler?.Stop()); // flushes the last phrase of each channel
        _subtitler?.Dispose();
        _subtitler = null;
        _punctuator?.Dispose();
        _punctuator = null;

        string? videoPath = null, notice = null;
        DateTime? videoStart = _video?.StartedUtc;
        if (_video is not null)
        {
            try
            {
                await _video.StopAsync();
                videoPath = Path.Combine(options.OutputFolder, _baseName + ".mp4");
                await VideoRecorder.MuxAsync(_ffmpeg!, _mkv!, micWav, loopWav, _video.StartedUtc, audioStart, videoPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("Video finalize failed", ex);
                videoPath = _mkv; // the raw capture stays on disk
                notice = $"The video could not be finalised ({ex.Message}). The raw capture was kept.";
            }
            finally { _video.Dispose(); _video = null; }
        }

        // subtitle files sit next to the video (or the audio) and are shifted so they line up with it
        var cues = Cues;
        double offset = SubtitleOffset(audioStart, videoStart);
        var stem = Path.Combine(options.OutputFolder, _baseName!);
        var txt = stem + ".txt";
        string? srt = options.WriteSrt ? stem + ".srt" : null;
        await File.WriteAllTextAsync(txt, SubtitleFormatter.ToText(cues), new System.Text.UTF8Encoding(false));
        if (srt is not null) await File.WriteAllTextAsync(srt, SubtitleFormatter.ToSrt(cues, offset), new System.Text.UTF8Encoding(false));

        string? audioPath = options.Mode == CaptureMode.SubtitlesOnly ? null : (micWav ?? loopWav);
        if (videoPath is not null) { _db.SetVideoPath(_itemId, videoPath); _db.SetSubtitleOffset(_itemId, offset); }

        CaptureGuards.KeepAwake(false);
        IsRecording = false;
        _recorder.Dispose();
        _recorder = null;
        var result = new RecordingResult(_itemId, _db.GetMeeting(_itemId)?.Title ?? "Recording", videoPath, audioPath, srt, txt, cues, duration, notice);

        // speaker names for the remote side run in the background; in "subtitles only" mode the audio is deleted afterwards
        _ = FinishAudioAsync(options, _itemId, micWav, loopWav, cues.Any(c => c.Speaker == "Others"));
        Status?.Invoke("Saved");
        Stopped?.Invoke(result);
        return result;
    }

    private async Task FinishAudioAsync(RecordingOptions options, long itemId, string? micWav, string? loopWav, bool hasRemoteSpeech)
    {
        try
        {
            if (hasRemoteSpeech && loopWav is not null && File.Exists(loopWav)) await LabelSpeakersAsync(itemId, loopWav);
        }
        catch (Exception ex) { AppLog.Warn("Speaker labeling skipped: " + ex.Message); }
        finally
        {
            if (options.Mode == CaptureMode.SubtitlesOnly)
                foreach (var f in new[] { micWav, loopWav })
                    try { if (f is not null && File.Exists(f)) File.Delete(f); } catch (IOException) { }
        }
    }

    private async Task LabelSpeakersAsync(long itemId, string loopbackWav)
    {
        var manager = new ModelManager(_http);
        await manager.EnsureAsync(ModelCatalog.DiarizationSegmentation);
        await manager.EnsureAsync(ModelCatalog.SpeakerEmbedding);
        await Task.Run(() =>
        {
            var audio = AudioFile.ReadMono16k(loopbackWav);
            using var diarizer = new Diarizer(Path.Combine(manager.PathFor(ModelCatalog.DiarizationSegmentation), "model.onnx"), manager.PathFor(ModelCatalog.SpeakerEmbedding));
            using var profiles = new SpeakerProfiles(manager.PathFor(ModelCatalog.SpeakerEmbedding), Path.Combine(AppPaths.Roaming, "voices", "voices.json"));
            SpeakerLabeling.Apply(_db, itemId, audio, diarizer, profiles);
        });
    }

    private static string Loc(string english) => english; // the UI layer shows its own localised messages; engine errors stay in English

    public void Dispose()
    {
        _watchdog?.Dispose();
        if (_recorder is not null) { _recorder.DataAvailable -= OnAudio; _recorder.Dispose(); }
        _video?.Dispose();
        _subtitler?.Dispose();
        _punctuator?.Dispose();
        if (IsRecording) CaptureGuards.KeepAwake(false);
    }
}
