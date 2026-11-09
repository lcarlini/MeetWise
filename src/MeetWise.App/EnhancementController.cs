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
    private bool _voiceLockSuspended;

    /// <summary>Raised on a background thread when the mode was lowered because processing could not keep up.</summary>
    public event Action<NoiseMode>? Downgraded;

    /// <summary>Raised on a background thread when voice lock was switched off for this session because it was slower than real time.</summary>
    public event Action? VoiceLockSuspended;

    /// <summary>Raised (any thread) when the route starts, stops or has to wait for a device.</summary>
    public event Action<string>? StatusChanged;

    public EnhancementController(HttpClient http) => _http = http;

    public bool IsRunning => _route is not null;
    public NoiseMode ActiveMode => _mode;
    public string Status => _route?.Status ?? "stopped";
    public string Stats => _route is null ? "" : Loc.T("mtg.status.stats", _route.RecentLoad.ToString("P0"), _route.Overflows, _route.Restarts);

    /// <param name="renderDeviceId">Where the cleaned audio goes. Null means the MeetWise Mic Feed (the virtual microphone).</param>
    public async Task StartAsync(AppSettings s, string? renderDeviceId = null, IProgress<double>? progress = null)
    {
        if (_route is not null) return;
        _renderId = renderDeviceId ?? VirtualMic.FindFeed()?.Id
            ?? throw new InvalidOperationException("The MeetWise Microphone is not installed yet. Click \"Create MeetWise Microphone\" first.");
        _settings = s;
        _mode = s.NoiseCancellation;
        _voiceLockSuspended = false;
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

    private Task<EnhancementOptions> BuildOptionsAsync(IProgress<double>? progress)
        => OptionsForAsync(_http, _settings!, _mode, _settings!.VoiceIsolation && !_voiceLockSuspended, progress);

    /// <summary>
    /// The processing chain for these settings, exactly as the live MeetWise Microphone runs it. The "Record and compare" button uses the same
    /// call, so what you hear there is what apps receive.
    /// </summary>
    /// <summary>What a set of options really does, in words, e.g. "voice lock + noise Max + echo cancellation".</summary>
    public static string Describe(EnhancementOptions o, NoiseMode mode)
    {
        var parts = new List<string>();
        if (o.Kind == EnhancerKind.VoiceIsolation) parts.Add(Loc.T("cmp.voicelock"));
        if (o.Kind is EnhancerKind.Light or EnhancerKind.FullBand || o.PostKind is not null) parts.Add(Loc.T("cmp.noise", Loc.T(mode switch { NoiseMode.Light => "noise.light", NoiseMode.Balanced => "noise.balanced", NoiseMode.Max => "noise.max", _ => "noise.off" })));
        if (o.EchoCancellation) parts.Add(Loc.T("cmp.echo"));
        return parts.Count == 0 ? Loc.T("cmp.nothing") : string.Join(" + ", parts);
    }

    /// <summary>What the live MeetWise Microphone applies right now (after any automatic downgrade).</summary>
    public string Applied => _options is null ? "" : Describe(_options, _mode);

    public static async Task<EnhancementOptions> OptionsForAsync(HttpClient http, AppSettings s, NoiseMode mode, bool voiceLock, IProgress<double>? progress)
    {
        var manager = new ModelManager(http);
        string? noisePath = null;
        var noiseKind = mode == NoiseMode.Light ? EnhancerKind.Light : EnhancerKind.FullBand;
        if (mode != NoiseMode.Off)
        {
            var model = mode switch
            {
                NoiseMode.Light => ModelCatalog.DenoiseLight,
                NoiseMode.Balanced => ModelCatalog.DenoiseBalanced,
                _ => ModelCatalog.DenoiseFullBand
            };
            await manager.EnsureAsync(model, progress);
            noisePath = manager.PathFor(model);
        }

        if (voiceLock && VoiceProfile.IsTrained())
        {
            // Voice lock only keeps the enrolled voice; it does not remove steady noise. Before 2026-11-09 it replaced the noise model, so with
            // voice lock on no noise cancellation ran at all. The chosen noise mode now always runs after it.
            await manager.EnsureAsync(ModelCatalog.VoiceIsolation, progress);
            return new EnhancementOptions(EnhancerKind.VoiceIsolation, null, s.EchoCancellation,
                manager.PathFor(ModelCatalog.VoiceIsolation), VoiceProfile.ClipPath(),
                Threads: Math.Clamp(Environment.ProcessorCount / 4, 2, 4), MinBlockMs: 80,
                PostKind: noisePath is null ? null : noiseKind, PostModelPath: noisePath);
        }
        if (noisePath is null) return new EnhancementOptions(null, null, s.EchoCancellation);
        return new EnhancementOptions(noiseKind, noisePath, s.EchoCancellation);
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
            bool voiceLockOn = _settings is { VoiceIsolation: true } && !_voiceLockSuspended && VoiceProfile.IsTrained();
            if (voiceLockOn && _mode is NoiseMode.Light or NoiseMode.Off)
            {
                // the noise stage is already as light as it gets: pause voice lock, the heaviest stage, for this session
                AppLog.Warn("Voice lock is slower than real time on this PC; switching it off for this session (the saved setting is kept).");
                _voiceLockSuspended = true;
                _options = await BuildOptionsAsync(null);
                _route?.DevicesChanged();
                VoiceLockSuspended?.Invoke();
                return;
            }
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
