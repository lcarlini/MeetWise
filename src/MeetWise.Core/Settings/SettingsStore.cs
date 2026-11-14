using System.Text.Json;
using System.Text.Json.Serialization;
using MeetWise.Core.Logging;

namespace MeetWise.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public SettingsStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.Roaming;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
            return Migrate(s);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Version 2 is the microphone-only MeetWise. Older files had "always on" off by default, which left the virtual microphone
    /// silent in Zoom/Teams unless a hidden button was pressed, so it is switched on once.
    /// </summary>
    public static AppSettings Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            s.AlwaysOnMicrophone = true;
            s.SettingsVersion = 2;
        }
        s.NoiseStrength = Math.Clamp(s.NoiseStrength, 0, 100);
        s.AccentBufferMs = Math.Clamp(s.AccentBufferMs, 80, 600);
        s.AccentStrength = Math.Clamp(s.AccentStrength, 0, 100);
        if (!Enum.IsDefined(s.Cleanup)) s.Cleanup = CleanupEngine.MeetWise;
        return s;
    }

    public void Save(AppSettings settings)
    {
        // Write then replace so a crash never leaves a truncated file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
