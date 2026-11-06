using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class EnhancerTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");
    private static string AsrDir => Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26");

    private static float[] LoadWav16k(string path)
    {
        using var r = new WaveFileReader(path);
        var sp = r.ToSampleProvider();
        var all = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return all.ToArray();
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    /// <summary>1.5 s of pure noise followed by speech + the same noise, at roughly 0 dB SNR.</summary>
    private static float[] NoisySpeech(float[] speech, int seed = 7)
    {
        var rng = new Random(seed);
        double speechRms = Rms(speech);
        var noise = new float[16000 * 3 / 2 + speech.Length];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)((rng.NextDouble() * 2 - 1) * speechRms * 1.5);
        var mix = new float[noise.Length];
        for (int i = 0; i < mix.Length; i++)
            mix[i] = noise[i] + (i >= 24000 ? speech[i - 24000] : 0f);
        return mix;
    }

    private static float[] Run(SpeechEnhancer e, float[] input)
    {
        var outp = new List<float>();
        for (int i = 0; i < input.Length; i += 1600)
            outp.AddRange(e.Process(input.AsSpan(i, Math.Min(1600, input.Length - i)).ToArray(), 16000));
        outp.AddRange(e.Flush());
        return outp.ToArray();
    }

    [Theory]
    [InlineData(EnhancerKind.Light, "gtcrn_simple.onnx")]
    [InlineData(EnhancerKind.FullBand, "dpdfnet2_48khz_hr.onnx")]
    public void Enhancer_ReducesNoise_AndKeepsSpeechIntelligible(EnhancerKind kind, string file)
    {
        var model = Path.Combine(Models, "enhance", file);
        if (!File.Exists(model) || !Directory.Exists(AsrDir)) return;
        var speech = LoadWav16k(Path.Combine(AsrDir, "test_wavs", "0.wav"));
        var noisy = NoisySpeech(speech);

        using var enh = new SpeechEnhancer(kind, model);
        var clean = Run(enh, noisy);
        Assert.True(clean.Length > noisy.Length / 2, "enhancer produced almost no output");

        // Noise-only region (first ~1.2 s after model warm-up) must get at least 6 dB quieter.
        int rate = enh.OutputSampleRate;
        var before = Rms(noisy.AsSpan(4000, 12000));
        var after = Rms(clean.AsSpan(rate / 4, rate * 3 / 4));
        double reductionDb = 20 * Math.Log10(before / Math.Max(after, 1e-9));
        Assert.True(reductionDb >= 6, $"{kind}: noise reduced only {reductionDb:0.0} dB");

        // The enhanced audio must still be recognizable.
        var finals = new List<string>();
        using var asr = new StreamingRecognizer(AsrDir, "Me", rate, 1);
        asr.Transcript += ev => { if (ev.IsFinal) finals.Add(ev.Text); };
        for (int i = 0; i < clean.Length; i += 3200)
            asr.Feed(clean.AsSpan(i, Math.Min(3200, clean.Length - i)));
        asr.Finish();
        var hyp = string.Join(" ", finals).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var reference = "AFTER EARLY NIGHTFALL THE YELLOW LAMPS WOULD LIGHT UP HERE AND THERE THE SQUALID QUARTER OF THE BROTHELS"
            .Split(' ').Distinct().ToArray();
        double recall = reference.Count(hyp.Contains) / (double)reference.Length;
        Assert.True(recall >= 0.6, $"{kind}: only {recall:P0} of reference words recognized after enhancement");
    }
}
