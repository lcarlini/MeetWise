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
            if (string.IsNullOrWhiteSpace(s.OutputFolder)) s.OutputFolder = AppPaths.DefaultRecordings;
            return s;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        // Write then replace so a crash never leaves a truncated file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
