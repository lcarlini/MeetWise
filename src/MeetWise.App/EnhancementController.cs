using System.IO;
using System.Net.Http;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>Owns the live "clean my microphone" route and the models it needs. Falls back to a lighter mode if the CPU cannot keep up.</summary>
public sealed class EnhancementController : IDisposable
{
    private readonly HttpClient _http;
    private AudioRoute? _route;
    private AppSettings? _settings;
    private string? _renderId;
    private NoiseMode _mode;
    private int _restarting;

    /// <summary>Raised on a background thread when the mode was lowered because processing could not keep up.</summary>
    public event Action<NoiseMode>? Downgraded;

    public EnhancementController(HttpClient http) => _http = http;

    public bool IsRunning => _route is not null;
    public NoiseMode ActiveMode => _mode;
    public string Stats => _route is null ? "" : $"load {_route.RecentLoad:P0} of real time, {_route.Overflows} dropouts";

    public async Task StartAsync(AppSettings s, string renderDeviceId, IProgress<double>? progress = null)
    {
        if (_route is not null) return;
        _settings = s;
        _renderId = renderDeviceId;
        _mode = s.NoiseCancellation;
        await BuildAndStartAsync(progress);
    }

    private async Task BuildAndStartAsync(IProgress<double>? progress)
    {
        var s = _settings!;
        var manager = new ModelManager(_http);
        EnhancementOptions options;
        if (s.VoiceIsolation && File.Exists(VoiceEnrollment.DefaultPath))
        {
            await manager.EnsureAsync(ModelCatalog.VoiceIsolation, progress);
            options = new EnhancementOptions(EnhancerKind.VoiceIsolation, null, s.EchoCancellation,
                manager.PathFor(ModelCatalog.VoiceIsolation), VoiceEnrollment.DefaultPath, Threads: 2);
        }
        else if (_mode == NoiseMode.Off)
        {
            options = new EnhancementOptions(null, null, s.EchoCancellation);
        }
        else
        {
            var model = _mode switch
            {
                NoiseMode.Light => ModelCatalog.DenoiseLight,
                NoiseMode.Balanced => ModelCatalog.DenoiseBalanced,
                _ => ModelCatalog.DenoiseFullBand
            };
            await manager.EnsureAsync(model, progress);
            options = new EnhancementOptions(EnhancerKind.FullBand, manager.PathFor(model), s.EchoCancellation);
            if (_mode == NoiseMode.Light) options = options with { Kind = EnhancerKind.Light };
        }

        var en = new MMDeviceEnumerator();
        var mic = AudioDeviceService.OpenCapture(s.MicrophoneId, en);
        var render = AudioDeviceService.OpenRender(_renderId!, en);
        LoopbackReference? reference = null;
        if (s.EchoCancellation)
            reference = new LoopbackReference(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        var route = new AudioRoute(mic, render, options, reference);
        route.Overloaded += () => _ = Task.Run(DowngradeAsync);
        _route = route;
        route.Start();
    }

    private async Task DowngradeAsync()
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        try
        {
            var next = _mode switch { NoiseMode.Max => NoiseMode.Balanced, NoiseMode.Balanced => NoiseMode.Light, _ => NoiseMode.Light };
            if (next == _mode) return; // already at the lightest model; nothing cheaper to switch to
            AppLog.Warn($"Enhancement overloaded in {_mode} mode; switching to {next}.");
            _route?.Dispose();
            _route = null;
            _mode = next;
            await BuildAndStartAsync(null);
            Downgraded?.Invoke(next);
        }
        catch (Exception ex)
        {
            AppLog.Error("Downgrade failed", ex);
        }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }

    public void Stop()
    {
        _route?.Dispose();
        _route = null;
    }

    public void Dispose() => Stop();
}
