using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class RealtimeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public RealtimeTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    private static float[] Load(string path, out int rate)
    {
        using var r = new WaveFileReader(path);
        rate = r.WaveFormat.SampleRate;
        var sp = r.ToSampleProvider();
        var all = new List<float>();
        var buf = new float[16000 * r.WaveFormat.Channels];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return AudioRoute48(all.ToArray(), r.WaveFormat.Channels);
    }

    private static float[] AudioRoute48(float[] x, int ch) => MeetWise.Audio.AudioRoute.ToMono(x, ch);

    [Theory]
    [InlineData(EnhancerKind.Light, "gtcrn_simple.onnx")]
    [InlineData(EnhancerKind.FullBand, "dpdfnet2_48khz_hr.onnx")]
    public void Engine_RunsFasterThanRealtime_With10msBlocks(EnhancerKind kind, string file)
    {
        var model = Path.Combine(Models, "enhance", file);
        if (!File.Exists(model)) return;
        using var engine = new EnhancementEngine(48000, new EnhancementOptions(kind, model, EchoCancellation: true));
        var rng = new Random(1);
        var block = new float[480];
        var far = new float[480];
        for (int i = 0; i < 1000; i++) // 10 s in 10 ms blocks, the size a WASAPI event delivers
        {
            for (int k = 0; k < block.Length; k++) { block[k] = (float)(rng.NextDouble() - 0.5) * 0.2f; far[k] = (float)(rng.NextDouble() - 0.5) * 0.2f; }
            engine.Process((float[])block.Clone(), (float[])far.Clone());
        }
        _out.WriteLine($"{kind} RTF={engine.RealTimeFactor:0.000}");
        Assert.True(engine.RealTimeFactor < 0.5, $"{kind} RTF {engine.RealTimeFactor:0.00} too slow for real time");
    }

    [Fact]
    public void VoiceIsolation_ProcessesCombinedAudio_AndRemovesEnergy()
    {
        var dir = Path.Combine(Models, "enhance", "sherpa-onnx-voxlock-int8");
        if (!Directory.Exists(dir)) return;
        var combined = Load(Path.Combine(dir, "test_wavs", "en-combined.wav"), out var rate);
        using var enh = new SpeechEnhancer(EnhancerKind.VoiceIsolation, dir, 2, Path.Combine(dir, "test_wavs", "en-voice-sample-f.wav"));
        var outp = new List<float>();
        for (int i = 0; i < combined.Length; i += rate / 10)
            outp.AddRange(enh.Process(combined.AsSpan(i, Math.Min(rate / 10, combined.Length - i)).ToArray(), rate));
        outp.AddRange(enh.Flush());
        double Rms(IEnumerable<float> v) { var a = v.ToArray(); return Math.Sqrt(a.Sum(x => (double)x * x) / Math.Max(1, a.Length)); }
        Assert.True(outp.Count > combined.Length / 2, "no output");
        Assert.True(Rms(outp) < Rms(combined) * 0.95, $"voice isolation did not remove other speakers: {Rms(outp):0.000} vs {Rms(combined):0.000}");
    }
}
