using System.Text;

namespace MeetWise.Core.Logging;

/// <summary>File logger: %AppData%\MeetWise\logs\meetwise-yyyyMMdd.log (14 days kept). Ported from RecBox.</summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static bool _cleaned;

    public static string LogDirectory { get; } = Path.Combine(AppPaths.Roaming, "logs");
    public static string CurrentLogFile => Path.Combine(LogDirectory, $"meetwise-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDirectory);
                if (!_cleaned) { _cleaned = true; Cleanup(); }
                File.AppendAllText(CurrentLogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void Cleanup()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var f in Directory.GetFiles(LogDirectory, "meetwise-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}
