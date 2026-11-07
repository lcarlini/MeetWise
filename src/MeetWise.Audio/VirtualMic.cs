using System.Runtime.InteropServices;
using MeetWise.Core.Logging;
using NAudio.CoreAudioApi;

namespace MeetWise.Audio;

public sealed record EndpointInfo(string Id, string Name, string Adapter, DataFlow Flow);

/// <summary>
/// Finds the virtual cable that backs the "MeetWise Microphone". Devices are recognised by their audio adapter name (the "VB-Audio Virtual Cable"
/// part), which does not change when the endpoint itself is renamed to "MeetWise Microphone".
/// </summary>
public static class VirtualMic
{
    public const string AdapterName = "VB-Audio Virtual Cable";
    public const string MicName = "MeetWise Microphone";
    public const string FeedName = "MeetWise Mic Feed";

    public static bool IsVirtualAdapter(string? adapter)
        => adapter?.Contains(AdapterName, StringComparison.OrdinalIgnoreCase) == true;

    public static IReadOnlyList<EndpointInfo> Endpoints(DataFlow flow, bool includeVirtual = true)
    {
        var list = new List<EndpointInfo>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var adapter = AdapterOf(d);
                if (includeVirtual || !IsVirtualAdapter(adapter)) list.Add(new EndpointInfo(d.ID, d.FriendlyName, adapter, flow));
            }
        }
        catch (Exception ex) { AppLog.Warn("Endpoint enumeration failed: " + ex.Message); }
        return list;
    }

    public static string AdapterOf(MMDevice d)
    {
        try { return d.Properties.Contains(PropertyKeys.PKEY_DeviceInterface_FriendlyName) ? d.Properties[PropertyKeys.PKEY_DeviceInterface_FriendlyName].Value?.ToString() ?? "" : ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>The capture side other apps record from (the "MeetWise Microphone"), or null when the cable is not installed.</summary>
    public static EndpointInfo? FindMic() => Endpoints(DataFlow.Capture).FirstOrDefault(e => IsVirtualAdapter(e.Adapter));

    /// <summary>The render side MeetWise writes the cleaned voice into (the "MeetWise Mic Feed").</summary>
    public static EndpointInfo? FindFeed() => Endpoints(DataFlow.Render).FirstOrDefault(e => IsVirtualAdapter(e.Adapter));

    public static bool IsInstalled() => FindMic() is not null && FindFeed() is not null;

    /// <summary>
    /// Picks the real microphone to clean. "default" means the Windows default, but when that is the virtual mic itself (the user set it as the
    /// system default) using it would feed the output back into the input, so the remembered physical mic (or the first real one) is used instead.
    /// </summary>
    public static string? ChoosePhysicalMic(string requestedId, string? windowsDefaultId, IReadOnlyList<EndpointInfo> captureDevices, string? rememberedPhysicalId)
    {
        var real = captureDevices.Where(d => !IsVirtualAdapter(d.Adapter)).ToList();
        string? wanted = requestedId is "default" or "" ? windowsDefaultId : requestedId;
        if (wanted is not null && real.Any(d => d.Id == wanted)) return wanted;
        if (rememberedPhysicalId is not null && real.Any(d => d.Id == rememberedPhysicalId)) return rememberedPhysicalId;
        return real.FirstOrDefault()?.Id;
    }

    public static string? WindowsDefaultCaptureId(Role role = Role.Communications)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return d.ID;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Sets and restores the Windows default microphone (the same switch as Settings, System, Sound, Input).</summary>
public static class DefaultMicrophone
{
    public sealed record Snapshot(string? Console, string? Multimedia, string? Communications);

    public static Snapshot Current() => new(
        VirtualMic.WindowsDefaultCaptureId(Role.Console),
        VirtualMic.WindowsDefaultCaptureId(Role.Multimedia),
        VirtualMic.WindowsDefaultCaptureId(Role.Communications));

    /// <summary>Makes the endpoint the default for every role. Returns false (with a log entry) if Windows refuses.</summary>
    public static bool Set(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) { AppLog.Warn($"SetDefaultEndpoint({role}) failed: 0x{hr:X8}"); return false; }
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not change the default microphone: " + ex.Message);
            return false;
        }
    }

    public static bool Restore(Snapshot previous)
    {
        bool ok = true;
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var (id, role) in new[] { (previous.Console, Role.Console), (previous.Multimedia, Role.Multimedia), (previous.Communications, Role.Communications) })
                if (id is not null) ok &= policy.SetDefaultEndpoint(id, role) == 0;
        }
        catch (Exception ex) { AppLog.Warn("Could not restore the default microphone: " + ex.Message); return false; }
        return ok;
    }

    // Windows' own (undocumented but long-stable) policy interface, the one the Sound control panel uses.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string name, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
