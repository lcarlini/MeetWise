namespace MeetWise.Core;

public static class AppPaths
{
    public static string Roaming { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetWise");

    public static string Local { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise");

    public static string Models => Path.Combine(Local, "models");
    public static string Database => Path.Combine(Local, "meetwise.db");
    public static string DefaultRecordings => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MeetWise");
}
