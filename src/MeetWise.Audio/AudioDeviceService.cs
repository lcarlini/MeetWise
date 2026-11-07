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
        // The virtual MeetWise Microphone is an output of MeetWise, never a source for it (that would loop the sound back).
        foreach (var d in VirtualMic.Endpoints(DataFlow.Capture, includeVirtual: false))
            devices.Add(new AudioDeviceInfo { Id = d.Id, Name = d.Name });
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

    /// <summary>
    /// Opens a capture device. "default" is the Windows default microphone, except when that default is the virtual MeetWise Microphone itself:
    /// then the remembered (or first) physical microphone is used, so the route can never record its own output.
    /// </summary>
    public static MMDevice OpenCapture(string id, MMDeviceEnumerator e, string? rememberedPhysicalId = null)
    {
        if (id != DefaultDeviceId && !string.IsNullOrWhiteSpace(id)) return e.GetDevice(id);
        var def = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        if (!VirtualMic.IsVirtualAdapter(VirtualMic.AdapterOf(def))) return def;
        var pick = VirtualMic.ChoosePhysicalMic(DefaultDeviceId, def.ID, VirtualMic.Endpoints(DataFlow.Capture), rememberedPhysicalId);
        return pick is null ? throw new InvalidOperationException("No physical microphone is available.") : e.GetDevice(pick);
    }
}
