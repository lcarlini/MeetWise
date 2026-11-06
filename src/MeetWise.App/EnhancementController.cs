using System.IO;
using System.Net.Http;
using MeetWise.Audio;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>Owns the live "clean my microphone" route and the models it needs.</summary>
public sealed class EnhancementController : IDisposable
{
    private readonly HttpClient _http;
    private AudioRoute? _route;

    public EnhancementController(HttpClient http) => _http = http;

    public bool IsRunning => _route is not null;
    public string Stats => _route is null ? "" : $"load {_route.RealTimeFactor:P0} of real time, {_route.Overflows} dropouts";

    public async Task StartAsync(AppSettings s, string renderDeviceId, IProgress<double>? progress = null)
    {
        if (_route is not null) return;
        var manager = new ModelManager(_http);
        EnhancementOptions options;
        var mode = s.NoiseCancellation;
        if (s.VoiceIsolation && File.Exists(VoiceEnrollment.DefaultPath))
        {
            await manager.EnsureAsync(ModelCatalog.VoiceIsolation, progress);
            options = new EnhancementOptions(EnhancerKind.VoiceIsolation, null, s.EchoCancellation,
                manager.PathFor(ModelCatalog.VoiceIsolation), VoiceEnrollment.DefaultPath, Threads: 2);
        }
        else if (mode == NoiseMode.Off)
        {
            options = new EnhancementOptions(null, null, s.EchoCancellation);
        }
        else
        {
            var model = mode == NoiseMode.Light ? ModelCatalog.DenoiseLight : ModelCatalog.DenoiseFullBand;
            await manager.EnsureAsync(model, progress);
            options = new EnhancementOptions(mode == NoiseMode.Light ? EnhancerKind.Light : EnhancerKind.FullBand,
                manager.PathFor(model), s.EchoCancellation, Threads: mode == NoiseMode.Max ? 2 : 1);
        }

        var en = new MMDeviceEnumerator();
        var mic = AudioDeviceService.OpenCapture(s.MicrophoneId, en);
        var render = AudioDeviceService.OpenRender(renderDeviceId, en);
        LoopbackReference? reference = null;
        if (s.EchoCancellation)
            reference = new LoopbackReference(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        _route = new AudioRoute(mic, render, options, reference);
        _route.Start();
    }

    public void Stop()
    {
        _route?.Dispose();
        _route = null;
    }

    public void Dispose() => Stop();
}
