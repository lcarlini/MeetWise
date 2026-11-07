using System.IO;
using System.Net.Http;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>
/// Runs the microphone clean-up: physical microphone, then echo/noise/voice-lock processing, then the "MeetWise Mic Feed" (the input side of the
/// virtual cable), so every app that records from the MeetWise Microphone gets the cleaned voice. The route heals itself when devices change,
/// never records its own output, and falls back to a lighter mode if the CPU cannot keep up.
/// </summary>
public sealed class EnhancementController : IDisposable
{
    private readonly HttpClient _http;
    private ResilientRoute? _route;
    private DeviceWatcher? _watcher;
    private AppSettings? _settings;
    private string? _renderId;
    private NoiseMode _mode;
    private EnhancementOptions? _options;
    private int _restarting;

    /// <summary>Raised on a background thread when the mode was lowered because processing could not keep up.</summary>
    public event Action<NoiseMode>? Downgraded;

    /// <summary>Raised (any thread) when the route starts, stops or has to wait for a device.</summary>
    public event Action<string>? StatusChanged;

    public EnhancementController(HttpClient http) => _http = http;

    public bool IsRunning => _route is not null;
    public NoiseMode ActiveMode => _mode;
    public string Status => _route?.Status ?? "stopped";
    public string Stats => _route is null ? "" : $"load {_route.RecentLoad:P0} of real time, {_route.Overflows} dropouts, {_route.Restarts} restart(s)";

    /// <param name="renderDeviceId">Where the cleaned audio goes. Null means the MeetWise Mic Feed (the virtual microphone).</param>
    public async Task StartAsync(AppSettings s, string? renderDeviceId = null, IProgress<double>? progress = null)
    {
        if (_route is not null) return;
        _renderId = renderDeviceId ?? VirtualMic.FindFeed()?.Id
            ?? throw new InvalidOperationException("The MeetWise Microphone is not installed yet. Click \"Create MeetWise Microphone\" first.");
        _settings = s;
        _mode = s.NoiseCancellation;
        _options = await BuildOptionsAsync(progress);

        var route = new ResilientRoute(CreateRoute);
        route.StatusChanged += st => StatusChanged?.Invoke(st);
        _route = route;
        await Task.Run(route.Start); // opening WASAPI devices can block for seconds when a driver is slow: never on the UI thread
        _watcher = new DeviceWatcher(() => _route?.DevicesChanged());
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>After sleep, hibernate or a Bluetooth reconnect, streams that were open before are stale: rebuild the route (debounced like any device change).</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        AppLog.Info("System resumed: rebuilding the microphone route.");
        _watcher?.Poke();
    }

    private async Task<EnhancementOptions> BuildOptionsAsync(IProgress<double>? progress)
    {
        var s = _settings!;
        var manager = new ModelManager(_http);
        if (s.VoiceIsolation && VoiceProfile.IsTrained())
        {
            await manager.EnsureAsync(ModelCatalog.VoiceIsolation, progress);
            return new EnhancementOptions(EnhancerKind.VoiceIsolation, null, s.EchoCancellation,
                manager.PathFor(ModelCatalog.VoiceIsolation), VoiceProfile.ClipPath(), Threads: 2);
        }
        if (_mode == NoiseMode.Off) return new EnhancementOptions(null, null, s.EchoCancellation);

        var model = _mode switch
        {
            NoiseMode.Light => ModelCatalog.DenoiseLight,
            NoiseMode.Balanced => ModelCatalog.DenoiseBalanced,
            _ => ModelCatalog.DenoiseFullBand
        };
        await manager.EnsureAsync(model, progress);
        var kind = _mode == NoiseMode.Light ? EnhancerKind.Light : EnhancerKind.FullBand;
        return new EnhancementOptions(kind, manager.PathFor(model), s.EchoCancellation);
    }

    /// <summary>Builds a fresh route against whatever devices exist right now (called at start and after every device change).</summary>
    private IAudioRoute CreateRoute()
    {
        var s = _settings!;
        using var en = new MMDeviceEnumerator();
        var captures = VirtualMic.Endpoints(DataFlow.Capture);
        var micId = VirtualMic.ChoosePhysicalMic(s.MicrophoneId, VirtualMic.WindowsDefaultCaptureId(), captures, s.PhysicalMicId)
            ?? throw new InvalidOperationException("No physical microphone is available.");
        var mic = en.GetDevice(micId);
        var render = en.GetDevice(_renderId!);

        // The loopback reference for echo cancellation must not be our own virtual feed.
        LoopbackReference? reference = null;
        if (s.EchoCancellation)
        {
            var speakers = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (!VirtualMic.IsVirtualAdapter(VirtualMic.AdapterOf(speakers))) reference = new LoopbackReference(speakers);
        }
        var route = new AudioRoute(mic, render, _options!, reference);
        route.Overloaded += () => _ = Task.Run(DowngradeAsync);
        return route;
    }

    private async Task DowngradeAsync()
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        try
        {
            var next = _mode switch { NoiseMode.Max => NoiseMode.Balanced, NoiseMode.Balanced => NoiseMode.Light, _ => NoiseMode.Light };
            if (next == _mode) return; // already at the lightest model; nothing cheaper to switch to
            AppLog.Warn($"Enhancement overloaded in {_mode} mode; switching to {next}.");
            _mode = next;
            _options = await BuildOptionsAsync(null);
            _route?.DevicesChanged(); // rebuilds the route with the lighter options
            Downgraded?.Invoke(next);
        }
        catch (Exception ex) { AppLog.Error("Downgrade failed", ex); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }

    public void Stop()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watcher?.Dispose();
        _watcher = null;
        _route?.Dispose();
        _route = null;
        StatusChanged?.Invoke("stopped");
    }

    public void Dispose() => Stop();
}
