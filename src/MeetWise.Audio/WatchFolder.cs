using System.Collections.Concurrent;
using System.Threading.Channels;
using MeetWise.Core.Logging;

namespace MeetWise.Audio;

public sealed record WatchPaths(string Incoming, string Output, string Processed, string Errors)
{
    /// <summary>The extractor's layout: "To Process" next to Output, Processed and Errors.</summary>
    public static WatchPaths For(string incoming)
    {
        var full = Path.GetFullPath(incoming.TrimEnd('\\', '/'));
        var baseDir = Path.GetDirectoryName(full) ?? full;
        return new WatchPaths(full, Path.Combine(baseDir, "Output"), Path.Combine(baseDir, "Processed"), Path.Combine(baseDir, "Errors"));
    }
}

/// <summary>
/// Drop audio or video files into "To Process" and they are transcribed one at a time: results go to Output, the finished source file is moved to
/// Processed, and a file that fails is moved to Errors together with a .error.log that says why. Files still being copied are left alone until they are complete.
/// </summary>
public sealed class WatchFolder : IDisposable
{
    public static readonly string[] MediaExtensions =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts", ".3gp",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"
    };

    private readonly WatchPaths _paths;
    private readonly Func<string, string, Task> _import;
    private readonly TimeSpan _poll;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private Task? _worker;

    public event Action<string>? Started;
    public event Action<string>? Finished;
    public event Action<string, Exception>? Failed;

    public int Pending => _known.Count;

    /// <param name="import">Transcribes (file, outputFolder). Throwing marks the file as failed.</param>
    public WatchFolder(WatchPaths paths, Func<string, string, Task> import, TimeSpan? pollInterval = null)
    {
        _paths = paths;
        _import = import;
        _poll = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        foreach (var d in new[] { _paths.Incoming, _paths.Output, _paths.Processed, _paths.Errors }) Directory.CreateDirectory(d);
        _worker = Task.Run(RunAsync);
        _watcher = new FileSystemWatcher(_paths.Incoming) { EnableRaisingEvents = true, IncludeSubdirectories = false };
        _watcher.Created += (_, _) => Scan();
        _watcher.Renamed += (_, _) => Scan();
        _timer = new Timer(_ => Scan(), null, TimeSpan.Zero, _poll); // polling also covers files that finish copying later
    }

    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when nothing else holds the file open for writing (a copy or download in progress keeps it locked).</summary>
    public static bool IsComplete(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Scan()
    {
        if (_cts.IsCancellationRequested) return;
        string[] files;
        try { files = Directory.GetFiles(_paths.Incoming).Where(IsMedia).OrderBy(File.GetCreationTimeUtc).ToArray(); }
        catch (IOException) { return; }
        foreach (var file in files)
            // files already queued or being processed are not even opened: the completeness check would lock them against the worker's move
            if (!_known.ContainsKey(file) && IsComplete(file) && _known.TryAdd(file, 0)) _queue.Writer.TryWrite(file);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var file in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    Started?.Invoke(file);
                    await _import(file, _paths.Output);
                    Move(file, _paths.Processed);
                    Finished?.Invoke(file);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn($"Watch folder: {Path.GetFileName(file)} failed: {ex.Message}");
                    MoveToErrors(file, ex);
                    Failed?.Invoke(file, ex);
                }
                finally { _known.TryRemove(file, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void MoveToErrors(string file, Exception ex)
    {
        try
        {
            var target = Move(file, _paths.Errors);
            File.WriteAllText(target + ".error.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex.GetType().Name}: {ex.Message}\n\n{ex}");
        }
        catch (IOException) { /* leave the file where it is; it is retried only after a restart */ }
    }

    /// <summary>Moves without ever overwriting: a name already taken gets a numeric suffix. A brief lock (antivirus, indexer) is retried.</summary>
    private static string Move(string file, string folder)
    {
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        for (int attempt = 0; ; attempt++)
        {
            var target = Path.Combine(folder, stem + ext);
            for (int i = 1; File.Exists(target); i++) target = Path.Combine(folder, $"{stem} ({i}){ext}");
            try
            {
                File.Move(file, target);
                return target;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(150); // somebody still has the file open for a moment
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _watcher?.Dispose();
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
