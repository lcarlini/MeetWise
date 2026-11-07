using Microsoft.Win32;

namespace MeetWise.Audio;

/// <summary>Starts MeetWise when the user signs in (per user, no administrator rights), so the MeetWise Microphone works before any meeting app opens.</summary>
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public StartupRegistration(string valueName = "MeetWise") => _valueName = valueName;

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(_valueName) is string;
        }
    }

    /// <param name="exePath">Program to start. The --minimized switch keeps it in the tray.</param>
    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(_valueName, $"\"{exePath}\" --minimized");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public void Set(bool enabled, string exePath)
    {
        if (enabled) Enable(exePath); else Disable();
    }
}
