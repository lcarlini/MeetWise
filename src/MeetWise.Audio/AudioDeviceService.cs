using NAudio.CoreAudioApi;

namespace MeetWise.Audio;

/// <summary>Enumerates Windows audio endpoints. Ported from RecBox.</summary>
public sealed class AudioDeviceService
{
    public const string DefaultDeviceId = "default";

    public bool IsSystemAudioAvailable()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            if (e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count == 0) return false;
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return true;
        }
        catch { return false; }
    }

    public IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new() { Id = DefaultDeviceId, Name = "Default Windows microphone", IsDefault = true }
        };
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                devices.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName });
        }
        catch { /* keep the Default entry when the audio subsystem is unavailable */ }
        return devices;
    }

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName });
        }
        catch { }
        return list;
    }

    /// <summary>True for endpoints that look like a virtual cable or a MeetWise virtual device.</summary>
    public static bool LooksVirtual(string name)
        => name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("MeetWise", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public static MMDevice OpenRender(string id, MMDeviceEnumerator e)
        => e.GetDevice(id);

    public static MMDevice OpenCapture(string id, MMDeviceEnumerator e)
        => id == DefaultDeviceId || string.IsNullOrWhiteSpace(id)
            ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : e.GetDevice(id);
}
