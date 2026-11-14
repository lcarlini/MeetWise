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

    /// <param name="exePath">Program to start. --audio-host runs only the background microphone (tray icon, no window).</param>
    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(_valueName, $"\"{exePath}\" --audio-host");
    }

    /// <summary>
    /// Older versions started the whole window at sign-in ("--minimized"). The microphone now lives in the background host: switch the
    /// argument and keep the program path that was registered. Returns true when the entry was changed.
    /// </summary>
    public bool MigrateLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(_valueName) is not string value || !value.EndsWith("--minimized", StringComparison.OrdinalIgnoreCase)) return false;
        key.SetValue(_valueName, value[..^"--minimized".Length] + "--audio-host");
        return true;
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
