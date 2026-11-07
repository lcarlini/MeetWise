namespace MeetWise.Core;

public static class AppPaths
{
    /// <summary>
    /// Set the MEETWISE_DATA environment variable to keep settings, logs, voices and the database in another folder (used for demos, screenshots
    /// and tests). Downloaded models stay in the normal place so they are never fetched twice.
    /// </summary>
    public static string? DataOverride { get; } = Environment.GetEnvironmentVariable("MEETWISE_DATA") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    public static string Roaming { get; } = DataOverride is { } o
        ? Path.Combine(o, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetWise");

    public static string Local { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise");

    public static string Models => Path.Combine(Local, "models");
    public static string Database => DataOverride is { } o ? Path.Combine(o, "meetwise.db") : Path.Combine(Local, "meetwise.db");
    public static string DefaultRecordings => DataOverride is { } o
        ? Path.Combine(o, "recordings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MeetWise");
}
