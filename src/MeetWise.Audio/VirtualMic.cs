using System.Runtime.InteropServices;
using MeetWise.Core.Logging;
using NAudio.CoreAudioApi;

namespace MeetWise.Audio;

public sealed record EndpointInfo(string Id, string Name, string Adapter, DataFlow Flow);

public enum CableState { Missing, Ready, MicOnly, FeedOnly, Disabled }

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
                // the adapter name of a device id never changes, and reading the property store is the slow part of every refresh
                var adapter = AdapterCache.GetOrAdd(d.ID, _ => AdapterOf(d));
                if (includeVirtual || !IsVirtualAdapter(adapter)) list.Add(new EndpointInfo(d.ID, d.FriendlyName, adapter, flow));
            }
        }
        catch (Exception ex) { AppLog.Warn("Endpoint enumeration failed: " + ex.Message); }
        return list;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AdapterCache = new();

    public static string AdapterOf(MMDevice d)
    {
        try { return d.Properties.Contains(PropertyKeys.PKEY_DeviceInterface_FriendlyName) ? d.Properties[PropertyKeys.PKEY_DeviceInterface_FriendlyName].Value?.ToString() ?? "" : ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>The capture side other apps record from (the "MeetWise Microphone"), or null when the cable is not installed.</summary>
    public static EndpointInfo? FindMic() => Pick(Endpoints(DataFlow.Capture));

    /// <summary>The render side MeetWise writes the cleaned voice into (the "MeetWise Mic Feed").</summary>
    public static EndpointInfo? FindFeed() => Pick(Endpoints(DataFlow.Render));

    /// <summary>The cable endpoint to use when several are active (a renamed "MeetWise ..." one wins over a leftover "CABLE ..." one).</summary>
    public static EndpointInfo? Pick(IEnumerable<EndpointInfo> endpoints)
        => endpoints.Where(e => IsVirtualAdapter(e.Adapter)).OrderByDescending(e => e.Name.StartsWith("MeetWise", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

    public static bool IsInstalled() => GetState() == CableState.Ready;

    /// <summary>Which sides of the cable Windows currently shows. A driver that lost its playback side still has a (silent) microphone.
    /// Endpoints switched off in the Sound control panel ("Disable") are reported as <see cref="CableState.Disabled"/>: Zoom and Teams do not list them at all.</summary>
    public static CableState GetState()
    {
        var state = StateOf(FindMic() is not null, FindFeed() is not null);
        if (state != CableState.Ready && DisabledEndpoints().Count > 0) return CableState.Disabled;
        return state;
    }

    /// <summary>Cable endpoints that exist but are disabled in Windows (Sound control panel, "Disable").</summary>
    public static IReadOnlyList<EndpointInfo> DisabledEndpoints()
    {
        var list = new List<EndpointInfo>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Disabled))
                {
                    var adapter = AdapterOf(d);
                    if (IsVirtualAdapter(adapter)) list.Add(new EndpointInfo(d.ID, d.FriendlyName, adapter, flow));
                }
        }
        catch (Exception ex) { AppLog.Warn("Disabled-endpoint enumeration failed: " + ex.Message); }
        return list;
    }

    /// <summary>Switches the disabled cable endpoints back on (the same as "Enable" in the Sound control panel). Returns how many were enabled.</summary>
    public static int EnableDisabledEndpoints()
    {
        int n = 0;
        foreach (var group in DisabledEndpoints().GroupBy(e => e.Flow))
        {
            // one endpoint per side: a stale "CABLE Input"/"CABLE Output" duplicate left by an earlier install stays off when a renamed one exists
            bool haveActive = group.Key == DataFlow.Capture ? FindMic() is not null : FindFeed() is not null;
            if (haveActive) continue;
            var ep = group.OrderByDescending(e => e.Name.StartsWith("MeetWise", StringComparison.OrdinalIgnoreCase)).First();
            if (DefaultMicrophone.SetEnabled(ep.Id, true)) { n++; AppLog.Info($"Enabled the disabled endpoint '{ep.Name}'."); }
        }
        return n;
    }

    /// <summary>
    /// Makes sure neither side of the cable is muted or turned down: a muted "MeetWise Microphone" (capture) or "MeetWise Mic Feed" (playback)
    /// sends silence to Zoom/Teams even though MeetWise itself works. Returns a description of what was fixed (empty when nothing was wrong).
    /// </summary>
    public static IReadOnlyList<string> FixLevels()
    {
        var fixes = new List<string>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var ep in new[] { FindMic(), FindFeed() })
            {
                if (ep is null) continue;
                using var d = e.GetDevice(ep.Id);
                var vol = d.AudioEndpointVolume;
                if (vol.Mute) { vol.Mute = false; fixes.Add($"'{ep.Name}' was muted"); }
                if (vol.MasterVolumeLevelScalar < 0.99f) { fixes.Add($"'{ep.Name}' was at {vol.MasterVolumeLevelScalar:P0}"); vol.MasterVolumeLevelScalar = 1f; }
            }
        }
        catch (Exception ex) { AppLog.Warn("Could not check the virtual microphone levels: " + ex.Message); }
        foreach (var f in fixes) AppLog.Info("Virtual microphone level fixed: " + f);
        return fixes;
    }

    /// <summary>The shared-mode formats of both sides. VB-CABLE passes samples straight through, so both sides must run at the same rate.</summary>
    public static (int MicRate, int MicChannels, int FeedRate, int FeedChannels)? Formats()
    {
        try
        {
            var mic = FindMic(); var feed = FindFeed();
            if (mic is null || feed is null) return null;
            using var e = new MMDeviceEnumerator();
            using var m = e.GetDevice(mic.Id);
            using var f = e.GetDevice(feed.Id);
            var mf = m.AudioClient.MixFormat; var ff = f.AudioClient.MixFormat;
            return (mf.SampleRate, mf.Channels, ff.SampleRate, ff.Channels);
        }
        catch (Exception ex) { AppLog.Warn("Could not read the virtual microphone formats: " + ex.Message); return null; }
    }

    public static CableState StateOf(bool hasMic, bool hasFeed) => (hasMic, hasFeed) switch
    {
        (true, true) => CableState.Ready,
        (true, false) => CableState.MicOnly,
        (false, true) => CableState.FeedOnly,
        _ => CableState.Missing
    };

    /// <summary>Which status text the microphone card shows. A dead default microphone is the worst case: every app hears silence.</summary>
    public static string StatusKey(CableState state, bool isDefault, bool running) => state switch
    {
        CableState.Missing => "mtg.status.notinstalled",
        CableState.MicOnly or CableState.FeedOnly => isDefault ? "mtg.status.brokendefault" : "mtg.status.broken",
        CableState.Disabled => "mtg.status.disabled",
        _ when !running && isDefault => "mtg.status.offdefault",
        _ when !running => "mtg.status.off",
        _ => "mtg.status.on"
    };

    public static bool IsBroken(CableState s) => s is CableState.MicOnly or CableState.FeedOnly or CableState.Disabled;

    /// <summary>
    /// Picks the real microphone to clean. "default" means the Windows default, but when that is the virtual mic itself (the user set it as the
    /// system default) using it would feed the output back into the input, so the remembered physical mic (or the first real one) is used instead.
    /// </summary>
    public static string? ChoosePhysicalMic(string requestedId, string? windowsDefaultId, IReadOnlyList<EndpointInfo> captureDevices, string? rememberedPhysicalId,
        IReadOnlySet<string>? unresponsive = null)
    {
        var real = captureDevices.Where(d => !IsVirtualAdapter(d.Adapter) && unresponsive?.Contains(d.Id) != true).ToList();
        var hardware = real.Where(d => !IsOtherProcessor(d)).ToList();
        bool explicitChoice = requestedId is not ("default" or "");
        if (explicitChoice && real.Any(d => d.Id == requestedId)) return requestedId; // the user picked it: respect it, even a virtual one
        // "default": the Windows default, unless that is another noise processor (NVIDIA Broadcast, Krisp, ...). MeetWise replaces those;
        // chaining two denoisers only adds delay, and the NVIDIA Broadcast devices on the user's PC did not even open.
        foreach (var id in new[] { windowsDefaultId, rememberedPhysicalId })
            if (id is not null && hardware.Any(d => d.Id == id)) return id;
        return hardware.FirstOrDefault()?.Id ?? real.FirstOrDefault()?.Id;
    }

    private static readonly string[] OtherProcessors = { "NVIDIA Broadcast", "Krisp", "VoiceMeeter", "Voicemod", "Steam Streaming", "SteelSeries Sonar", "Camo", "Virtual" };

    public const string BroadcastName = "NVIDIA Broadcast";

    public static bool IsBroadcast(EndpointInfo d)
        => d.Adapter.Contains(BroadcastName, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(BroadcastName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The "Microphone (NVIDIA Broadcast)" among these capture endpoints, or null.</summary>
    public static EndpointInfo? FindBroadcastMic(IEnumerable<EndpointInfo> captures)
        => captures.FirstOrDefault(d => !IsVirtualAdapter(d.Adapter) && IsBroadcast(d));

    public static EndpointInfo? FindBroadcastMic() => FindBroadcastMic(Endpoints(DataFlow.Capture));

    /// <summary>True while the NVIDIA Broadcast app runs (its microphone only carries processed audio then).</summary>
    public static bool BroadcastAppRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("NVIDIA Broadcast").Length > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>A virtual microphone made by another app (noise remover, mixer, streaming tool), recognised by its adapter or name.</summary>
    public static bool IsOtherProcessor(EndpointInfo d)
        => OtherProcessors.Any(p => d.Adapter.Contains(p, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(p, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Enables or disables an endpoint, like the Sound control panel does. Returns false (with a log entry) if Windows refuses.</summary>
    public static bool SetEnabled(string deviceId, bool enabled)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr = policy.SetEndpointVisibility(deviceId, enabled);
            if (hr != 0) AppLog.Warn($"SetEndpointVisibility failed: 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex) { AppLog.Warn("Could not enable the endpoint: " + ex.Message); return false; }
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
