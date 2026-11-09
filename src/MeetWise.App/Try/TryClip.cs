using System.IO;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetWise.App.Try;

/// <summary>A short recording (or opened file) that every "Try it" panel can work on. Both channels are mono 16 kHz.</summary>
public sealed class TryClipData
{
    public required float[] Mic { get; init; }
    /// <summary>What the PC was playing while the microphone recorded (the echo reference). Null when it was not captured.</summary>
    public float[]? Loopback { get; init; }
    public string Source { get; init; } = "";
    public double Seconds => Mic.Length / 16000.0;
}

/// <summary>Records and loads test clips, and remembers the last one so a clip recorded once can be tried on every feature.</summary>
public static class TryClip
{
    public const double MaxFileSeconds = 60;

    public static TryClipData? Last { get; private set; }
    public static event Action? LastChanged;
    public static string Folder => Path.Combine(AppPaths.Roaming, "try");

    public static void Set(TryClipData clip)
    {
        Last = clip;
        try
        {
            Directory.CreateDirectory(Folder);
            Save(Path.Combine(Folder, "last-mic.wav"), clip.Mic);
            if (clip.Loopback is not null) Save(Path.Combine(Folder, "last-loopback.wav"), clip.Loopback);
        }
        catch (Exception ex) { AppLog.Warn("Could not keep the test clip: " + ex.Message); }
        LastChanged?.Invoke();
    }

    /// <summary>The clip from the previous session, if any.</summary>
    public static TryClipData? LoadPrevious()
    {
        try
        {
            var mic = Path.Combine(Folder, "last-mic.wav");
            if (!File.Exists(mic)) return null;
            var lb = Path.Combine(Folder, "last-loopback.wav");
            return new TryClipData { Mic = AudioFile.ReadMono16k(mic), Loopback = File.Exists(lb) ? AudioFile.ReadMono16k(lb) : null, Source = "previous recording" };
        }
        catch (Exception ex) { AppLog.Warn("Could not load the previous test clip: " + ex.Message); return null; }
    }

    public static void Save(string path, float[] samples16k)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        w.WriteSamples(samples16k, 0, samples16k.Length);
    }

    /// <summary>Opens any audio or video file, keeping the first <see cref="MaxFileSeconds"/> seconds.</summary>
    public static TryClipData FromFile(string path)
    {
        var all = AudioFile.ReadMono16k(path);
        int max = (int)(MaxFileSeconds * 16000);
        return new TryClipData { Mic = all.Length > max ? all[..max] : all, Source = Path.GetFileName(path) };
    }
}

/// <summary>One recording in progress: the physical microphone, and optionally what the PC is playing.</summary>
public sealed class TryRecording : IDisposable
{
    private readonly DualChannelRecorder _rec = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mw-try-" + Guid.NewGuid().ToString("N"));
    private readonly bool _loopback;

    public event Action<float>? Level;

    public TryRecording(string micId, bool captureSystemAudio)
    {
        _loopback = captureSystemAudio;
        Directory.CreateDirectory(_dir);
        _rec.Start(_dir, "clip", micId, captureMic: true, captureLoopback: captureSystemAudio);
        _rec.DataAvailable += (source, fmt, buf, n) =>
        {
            if (source != "mic" && source != "Me" && !source.StartsWith("mic", StringComparison.OrdinalIgnoreCase)) return;
            var f = LiveTranscriber.ToFloat(buf, n, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            float peak = 0;
            foreach (var v in f) peak = Math.Max(peak, Math.Abs(v));
            Level?.Invoke(peak);
        };
    }

    public TryClipData Stop(string source)
    {
        _rec.Stop();
        var mic = _rec.MicPath is { } m && File.Exists(m) ? AudioFile.ReadMono16k(m) : Array.Empty<float>();
        float[]? lb = _loopback && _rec.LoopbackPath is { } l && File.Exists(l) ? AudioFile.ReadMono16k(l) : null;
        return new TryClipData { Mic = mic, Loopback = lb, Source = source };
    }

    public void Dispose()
    {
        _rec.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>Plays test audio on the default speakers, one clip at a time.</summary>
public static class TryPlayer
{
    private static PcmPlayer? _player;
    private static CancellationTokenSource? _cts;
    private static MMDeviceEnumerator? _enum;

    public static event Action? Finished;

    public static void Play(float[] samples, int rate)
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            _enum ??= new MMDeviceEnumerator();
            using var device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var player = _player = new PcmPlayer(device, rate);
            player.Enqueue(samples);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(samples.Length / (double)rate + 0.4), cts.Token); }
                catch (OperationCanceledException) { return; }
                if (ReferenceEquals(_player, player)) { Stop(); Finished?.Invoke(); }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Test playback failed: " + ex.Message);
            Stop();
            throw;
        }
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        var p = _player;
        _player = null;
        try { p?.Dispose(); } catch (Exception) { }
    }
}
