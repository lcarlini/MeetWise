using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetWise.Audio;

/// <summary>What the clean microphone is doing, for the window, the tray and the pipe.</summary>
public sealed record MicStatus(
    CableState Cable,
    bool Running,
    string State,
    string? PhysicalMic,
    string Applied,
    RouteMeter? Meter,
    int Restarts,
    string? LastProblem,
    IReadOnlyList<string> Fixes,
    string? Accent = null);

/// <summary>Result of the end-to-end check: a known signal written into the MeetWise Mic Feed and recorded back from the MeetWise Microphone.</summary>
public sealed record CableCheck(bool Passed, double Correlation, double LevelDb, double DelayMs, string Message);

/// <summary>
/// The clean microphone, without any UI: physical microphone → high-pass → echo cancellation → noise removal / voice lock → strength → gain → limiter
/// → "MeetWise Mic Feed" (the playback side of the virtual cable), which every meeting app hears as the "MeetWise Microphone".
/// The output always goes to the feed (an earlier version let a hidden device list pick the speakers instead, so Zoom/Teams heard silence).
/// The route rebuilds itself after device changes, sleep and failures, steps down to a lighter model when the CPU cannot keep up, and fixes a
/// disabled, muted or turned-down virtual cable on start.
/// </summary>
public sealed class MicService : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private ResilientRoute? _route;
    private AudioRoute? _current;
    private DeviceWatcher? _watcher;
    private AppSettings _settings = new();
    private NoiseMode _mode;
    private EnhancementOptions? _options;
    private int _restarting;
    private bool _voiceLockSuspended;
    private string? _physicalName;
    private string? _lastProblem, _deviceProblem;
    private IReadOnlyList<string> _fixes = Array.Empty<string>();
    private readonly Dictionary<string, DateTime> _unresponsive = new();

    /// <summary>Microphones that hung within the last minute. Entries expire: when the whole Windows audio service froze (seen on the user's PC,
    /// 2026-11-13) every device hangs, and after Windows restarts the service they all work again.</summary>
    private IReadOnlySet<string> Unresponsive()
    {
        lock (_unresponsive)
        {
            foreach (var k in _unresponsive.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromSeconds(60)).Select(kv => kv.Key).ToList()) _unresponsive.Remove(k);
            return _unresponsive.Keys.ToHashSet();
        }
    }

    public event Action? Changed;

    public MicService(HttpClient http) => _http = http;

    public bool IsRunning { get { lock (_gate) return _route is not null; } }

    /// <summary>Starts (or restarts with new settings). Downloads the models the first time.</summary>
    public async Task StartAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(Stop, ct); // stopping joins audio threads: never on a UI thread
        _settings = settings.Clone();
        _mode = settings.NoiseCancellation;
        _voiceLockSuspended = false;
        _lastProblem = null;

        // A cable disabled in Windows is NOT switched back on here: changing endpoints while another audio driver (NVIDIA Broadcast) runs
        // was followed by the Windows audio service hanging on the user's PC (2026-11-07 and 2026-11-13). The window's Repair button does it,
        // on request, with a warning.
        var state = VirtualMic.GetState();
        if (state != CableState.Ready)
        {
            _lastProblem = state switch
            {
                CableState.Missing => "The MeetWise Microphone is not installed yet.",
                CableState.Disabled => "The MeetWise Microphone is disabled in Windows, so Zoom and Teams cannot see it. Click Repair to switch it back on.",
                _ => "The virtual microphone lost one of its sides. Use Repair."
            };
            Changed?.Invoke();
            throw new InvalidOperationException(_lastProblem);
        }
        var fixes = VirtualMic.FixLevels().ToList();
        if (VirtualMic.Formats() is { } f && f.MicRate != f.FeedRate)
            fixes.Add($"the two sides of the cable run at different rates ({f.FeedRate} Hz in, {f.MicRate} Hz out): set both to 48000 Hz in Sound settings");
        _fixes = fixes;

        _useBroadcast = settings.Cleanup == CleanupEngine.NvidiaBroadcast && VirtualMic.FindBroadcastMic() is not null;
        _options = await BuildOptionsAsync(progress);
        var route = new ResilientRoute(CreateRoute);
        route.StatusChanged += st => { if (st == "running") _deviceProblem = null; Changed?.Invoke(); };
        lock (_gate) _route = route;
        await Task.Run(route.Start, ct);
        _watcher = new DeviceWatcher(() => { lock (_gate) _route?.DevicesChanged(); });
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Changed?.Invoke();
    }

    public void Stop()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watcher?.Dispose();
        _watcher = null;
        ResilientRoute? r;
        lock (_gate) { r = _route; _route = null; _current = null; }
        r?.Dispose();
        DisposeAccent();
        Changed?.Invoke();
    }

    public MicStatus Status()
    {
        ResilientRoute? r; AudioRoute? cur;
        lock (_gate) { r = _route; cur = _current; }
        return new MicStatus(VirtualMic.GetState(), r is not null, r?.Status ?? "stopped", _physicalName,
            _options is null ? "" : (_useBroadcast ? "NVIDIA Broadcast (noise + room echo) + " : "") + Describe(_options, _mode), cur?.TakeMeter(), r?.Restarts ?? 0, _deviceProblem ?? _lastProblem ?? cur?.EchoProblem, _fixes,
            _accent is { } a ? $"{a.State}, +{a.DelayMs:0} ms, original voice used {a.FallbackShare:P0} of the time" : null);
    }

    /// <summary>Peaks since the last call, without the slow device enumeration of <see cref="Status"/> (for 20 fps level meters).</summary>
    public RouteMeter? Meter()
    {
        AudioRoute? cur;
        lock (_gate) cur = _current;
        return cur?.TakeMeter();
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        AppLog.Info("System resumed: rebuilding the microphone route.");
        _watcher?.Poke();
    }

    private Task<EnhancementOptions> BuildOptionsAsync(IProgress<double>? progress)
        => _useBroadcast
            ? Task.FromResult(BroadcastOptions(_settings))
            : OptionsForAsync(_http, _settings, _mode, _settings.VoiceIsolation && !_voiceLockSuspended, progress);

    /// <summary>NVIDIA Broadcast already removed noise and room echo: no denoiser, no voice lock, no high-pass. Speaker-echo cancellation and
    /// automatic volume follow the settings; the limiter always runs.</summary>
    public static EnhancementOptions BroadcastOptions(AppSettings s)
        => new(null, null, s.EchoCancellation, AutoGain: s.AutoGain, LookAheadLimiter: true);

    private bool _useBroadcast;

    /// <summary>What a set of options really does, e.g. "voice lock + noise (Max, 100%) + echo + auto gain".</summary>
    public static string Describe(EnhancementOptions o, NoiseMode mode)
    {
        var parts = new List<string>();
        if (o.Kind == EnhancerKind.VoiceIsolation) parts.Add("voice lock");
        if (o.Kind is EnhancerKind.Light or EnhancerKind.FullBand || o.PostKind is not null) parts.Add($"noise ({mode}, {o.NoiseStrength}%)");
        if (o.EchoCancellation) parts.Add("echo");
        if (o.AutoGain) parts.Add("auto gain");
        return parts.Count == 0 ? "limiter only" : string.Join(" + ", parts);
    }

    /// <summary>The processing chain for these settings, exactly as the live MeetWise Microphone runs it (tests and the benchmark use the same call).</summary>
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
            // voice lock keeps only the enrolled voice but does not remove steady noise: the chosen noise mode always runs after it
            var vox = ModelCatalog.VoiceIsolationFor();
            await manager.EnsureAsync(vox, progress);
            return new EnhancementOptions(EnhancerKind.VoiceIsolation, null, s.EchoCancellation,
                manager.PathFor(vox), VoiceProfile.ClipPath(),
                Threads: Math.Clamp(Environment.ProcessorCount / 4, 2, 4), MinBlockMs: 80,
                PostKind: noisePath is null ? null : noiseKind, PostModelPath: noisePath,
                NoiseStrength: s.NoiseStrength, HighPass: true, AutoGain: s.AutoGain, LookAheadLimiter: true);
        }
        if (noisePath is null) return new EnhancementOptions(null, null, s.EchoCancellation, HighPass: true, AutoGain: s.AutoGain, LookAheadLimiter: true);
        return new EnhancementOptions(noiseKind, noisePath, s.EchoCancellation, NoiseStrength: s.NoiseStrength, HighPass: true, AutoGain: s.AutoGain, LookAheadLimiter: true);
    }

    /// <summary>Builds a fresh route against whatever devices exist right now (called at start and after every device change).</summary>
    private IAudioRoute CreateRoute()
    {
        var s = _settings;
        using var en = new MMDeviceEnumerator();
        var feed = VirtualMic.FindFeed() ?? throw new InvalidOperationException("The MeetWise Mic Feed is missing.");
        var captures = VirtualMic.Endpoints(DataFlow.Capture);
        var bad = Unresponsive();
        // NVIDIA Broadcast mode: read its microphone. If it is missing or hung, fall back to the hardware mic with MeetWise's own cleanup
        // (never a silent call); the next device change switches back.
        bool wantBroadcast = s.Cleanup == CleanupEngine.NvidiaBroadcast;
        var broadcast = wantBroadcast ? VirtualMic.FindBroadcastMic(captures) : null;
        bool useBroadcast = broadcast is not null && !bad.Contains(broadcast.Id);
        if (useBroadcast != _useBroadcast)
        {
            _useBroadcast = useBroadcast;
            _options = BuildOptionsAsync(null).GetAwaiter().GetResult();
        }
        if (wantBroadcast && !useBroadcast)
            _deviceProblem = "NVIDIA Broadcast is not available, so MeetWise is cleaning the microphone itself.";
        if (useBroadcast) bad = bad.Append(broadcast!.Id).ToHashSet(); // only used below when not in Broadcast mode
        var micId = useBroadcast ? broadcast!.Id
            : VirtualMic.ChoosePhysicalMic(s.MicrophoneId, VirtualMic.WindowsDefaultCaptureId(), captures, s.PhysicalMicId, bad);
        if (micId is null && Unresponsive().Count > 0)
        {
            _deviceProblem = "Windows' audio service is not responding (every microphone hangs when opened). MeetWise keeps retrying; Windows normally restarts the service within a minute.";
            throw new InvalidOperationException(_deviceProblem);
        }
        if (micId is null) throw new InvalidOperationException("No physical microphone is available.");
        var mic = en.GetDevice(micId);
        var render = en.GetDevice(feed.Id);
        _physicalName = mic.FriendlyName;

        // echo: listen to what the meeting app plays. Zoom/Teams use the Communications default, other apps the Multimedia one.
        LoopbackReference? reference = null;
        if (s.EchoCancellation)
        {
            var speakers = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            if (!VirtualMic.IsVirtualAdapter(VirtualMic.AdapterOf(speakers))) reference = new LoopbackReference(speakers);
        }
        var route = new AudioRoute(mic, render, _options!, reference, AccentFor);
        route.Overloaded += () => _ = Task.Run(DowngradeAsync);
        route.DeviceHung += id =>
        {
            // the next rebuild (the route retries by itself) picks another microphone
            lock (_unresponsive) _unresponsive[id] = DateTime.UtcNow;
            _deviceProblem = $"'{_physicalName}' does not respond, so MeetWise is trying another microphone.";
        };
        lock (_gate) _current = route;
        return route;
    }

    private AccentStage? _accent;
    private int _accentRate;

    /// <summary>The Accent Assist stage for this output rate. The worker (a GPU model) is kept across route rebuilds; it is only restarted
    /// when the chain's rate changes.</summary>
    private AccentStage? AccentFor(int rate)
    {
        if (!_settings.AccentAssist || AccentPack.Bank is not { } bank || !AccentPack.IsInstalled) { DisposeAccent(); return null; }
        if (_accent is not null && _accentRate == rate) return _accent;
        DisposeAccent();
        _accent = new AccentStage(rate, _settings.AccentBufferMs, _settings.AccentStrength / 100f, bank);
        _accentRate = rate;
        _accent.Start();
        return _accent;
    }

    private void DisposeAccent()
    {
        _accent?.Dispose();
        _accent = null;
    }

    private async Task DowngradeAsync()
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        try
        {
            bool voiceLockOn = _settings.VoiceIsolation && !_voiceLockSuspended && VoiceProfile.IsTrained();
            if (voiceLockOn && _mode is NoiseMode.Light or NoiseMode.Off)
            {
                AppLog.Warn("Voice lock is slower than real time on this PC; switching it off for this session (the saved setting is kept).");
                _voiceLockSuspended = true;
                _lastProblem = "Voice lock was too heavy for this PC right now, so it was paused. Noise removal is still on.";
            }
            else
            {
                var next = _mode switch { NoiseMode.Max => NoiseMode.Balanced, NoiseMode.Balanced => NoiseMode.Light, _ => NoiseMode.Light };
                if (next == _mode) return;
                AppLog.Warn($"Enhancement overloaded in {_mode} mode; switching to {next}.");
                _mode = next;
                _lastProblem = $"Your PC could not keep up, so noise removal switched to {next}.";
            }
            _options = await BuildOptionsAsync(null);
            lock (_gate) _route?.DevicesChanged();
            Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Error("Downgrade failed", ex); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }

    /// <summary>
    /// Records from the "MeetWise Microphone" (exactly what Zoom/Teams record) while the route runs. Used by "Test as Zoom hears it".
    /// </summary>
    public static async Task<(float[] Samples, int Rate)> RecordVirtualMicAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var mic = VirtualMic.FindMic() ?? throw new InvalidOperationException("The MeetWise Microphone is not available.");
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDevice(mic.Id);
        using var cap = new WasapiCapture(dev, true, 20);
        var fmt = cap.WaveFormat;
        var all = new List<float>();
        cap.DataAvailable += (_, e) =>
        {
            var f = Pcm.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono = AudioRoute.ToMono(f, fmt.Channels);
            lock (all) all.AddRange(mono);
        };
        cap.StartRecording();
        try { await Task.Delay(duration, ct); }
        finally { cap.StopRecording(); }
        lock (all) return (all.ToArray(), fmt.SampleRate);
    }

    /// <summary>
    /// End-to-end check without anyone speaking: a 1 s chirp is mixed into the output and recorded back from the MeetWise Microphone.
    /// Passes when it arrives with good correlation and a usable level. This is the test that proves Zoom/Teams receive audio.
    /// </summary>
    public async Task<CableCheck> CheckCableAsync(CancellationToken ct = default)
    {
        AudioRoute? cur;
        lock (_gate) cur = _current;
        if (cur is null) return new CableCheck(false, 0, -120, 0, "The clean microphone is not running.");
        int rate = cur.OutputSampleRate;
        var probe = Chirp(rate, 1.0, 0.25f);
        var recording = RecordVirtualMicAsync(TimeSpan.FromSeconds(2.2), ct);
        await Task.Delay(300, ct);
        cur.InjectProbe(probe);
        var (rec, recRate) = await recording;
        var reference = recRate == rate ? probe : new StreamResampler(rate, recRate).Process(probe);
        var (corr, lag) = BestCorrelation(reference, rec);
        double level = AudioMetrics.PeakDb(rec);
        bool ok = corr > 0.6 && level > -40;
        string msg = ok
            ? $"The MeetWise Microphone receives MeetWise's audio (match {corr:P0}, peak {level:0} dBFS)."
            : level <= -90 ? "The MeetWise Microphone is silent: the audio MeetWise writes does not arrive. Use Repair."
            : $"The MeetWise Microphone receives something, but not MeetWise's test signal (match {corr:P0}, peak {level:0} dBFS).";
        AppLog.Info("Cable check: " + msg);
        return new CableCheck(ok, corr, level, lag * 1000.0 / recRate, msg);
    }

    internal static float[] Chirp(int rate, double seconds, float amplitude)
    {
        int n = (int)(rate * seconds);
        var x = new float[n];
        double f0 = 300, f1 = 3400, k = (f1 - f0) / seconds;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate;
            double env = Math.Min(1, Math.Min(t, seconds - t) / 0.02);
            x[i] = (float)(amplitude * env * Math.Sin(2 * Math.PI * (f0 * t + k * t * t / 2)));
        }
        return x;
    }

    /// <summary>Normalised cross-correlation peak of a known signal inside a recording (coarse search, then refined).</summary>
    internal static (double Correlation, int Lag) BestCorrelation(float[] reference, float[] recording)
    {
        if (recording.Length < reference.Length) return (0, 0);
        double er = 0;
        foreach (var v in reference) er += v * v;
        double best = 0; int bestLag = 0;
        int maxLag = recording.Length - reference.Length;
        for (int pass = 0; pass < 2; pass++)
        {
            int from = pass == 0 ? 0 : Math.Max(0, bestLag - 8), to = pass == 0 ? maxLag : Math.Min(maxLag, bestLag + 8), step = pass == 0 ? 4 : 1;
            for (int lag = from; lag <= to; lag += step)
            {
                double c = 0, ey = 0;
                for (int i = 0; i < reference.Length; i += 2) { float y = recording[lag + i]; c += reference[i] * y; ey += y * y; }
                double norm = c / Math.Sqrt(er / 2 * ey + 1e-12);
                if (norm > best) { best = norm; bestLag = lag; }
            }
        }
        return (best, bestLag);
    }

    public void Dispose() => Stop();
}
