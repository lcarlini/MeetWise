using MeetWise.Speech;

namespace MeetWise.Tests;

public class EchoTests
{
    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    // Far-end: band-limited noise with speech-like slow envelope. Echo path: 60 ms delay + decaying reverb.
    private static (float[] far, float[] echo) Synthesize(int rate, int seconds, int seed)
    {
        var rng = new Random(seed);
        int n = rate * seconds;
        var far = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            lp = 0.7f * lp + 0.3f * (float)(rng.NextDouble() * 2 - 1);
            far[i] = lp * (0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 3 * i / rate));
        }
        var h = new float[rate / 10];
        int delay = rate * 60 / 1000;
        for (int k = delay; k < h.Length; k++)
            h[k] = (float)((rng.NextDouble() * 2 - 1) * 0.5 * Math.Exp(-(k - delay) / (rate * 0.02)));
        var echo = new float[n];
        for (int i = 0; i < n; i++)
        {
            float acc = 0;
            for (int k = delay; k < h.Length && k <= i; k++) acc += h[k] * far[i - k];
            echo[i] = acc;
        }
        return (far, echo);
    }

    [Fact]
    public void Aec_RemovesEcho_WithGoodErle()
    {
        const int rate = 16000;
        var (far, echo) = Synthesize(rate, 12, 3);
        var aec = new EchoCanceller(rate, tailMs: 100, mu: 0.5f);
        var outp = new List<float>();
        for (int i = 0; i < far.Length; i += 160)
            outp.AddRange(aec.Process(echo.AsSpan(i, 160), far.AsSpan(i, 160)));

        int tail = rate * 4; // last 4 s, after convergence
        var before = Rms(echo.AsSpan(far.Length - tail));
        var after = Rms(outp.ToArray().AsSpan(far.Length - tail));
        double erle = 20 * Math.Log10(before / Math.Max(after, 1e-9));
        Assert.True(erle >= 15, $"ERLE only {erle:0.0} dB");
    }

    [Fact]
    public void Aec_PreservesNearEndSpeech_DuringDoubleTalk()
    {
        const int rate = 16000;
        var (far, echo) = Synthesize(rate, 14, 5);
        var rng = new Random(11);
        var near = new float[far.Length];
        float lp = 0;
        for (int i = 0; i < near.Length; i++)
        {
            lp = 0.9f * lp + 0.1f * (float)(rng.NextDouble() * 2 - 1);
            near[i] = lp * 3f; // strong near-end talker, starts at 8 s
            if (i < rate * 8) near[i] = 0;
        }
        var mic = new float[far.Length];
        for (int i = 0; i < mic.Length; i++) mic[i] = echo[i] + near[i];

        var aec = new EchoCanceller(rate);
        var outp = new List<float>();
        for (int i = 0; i < far.Length; i += 160)
            outp.AddRange(aec.Process(mic.AsSpan(i, 160), far.AsSpan(i, 160)));

        var kept = Rms(outp.ToArray().AsSpan(rate * 9, rate * 4));
        var wanted = Rms(near.AsSpan(rate * 9, rate * 4));
        Assert.InRange(kept / wanted, 0.7, 1.3); // near-end voice level survives
    }
}
