namespace MeetWise.Tests;

/// <summary>
/// Synthetic signals for echo-cancellation tests: speech-like talkers (harmonics under moving formants with syllable gating), noise types,
/// and a room-like echo path (bulk delay + decaying reverb). Deterministic for a given seed.
/// </summary>
internal static class EchoSim
{
    public static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    public static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

    /// <summary>Speech-like talker: 100-250 Hz voiced source with wandering pitch, three formant resonators, and syllable/sentence gating with pauses.</summary>
    public static float[] Speech(int rate, double seconds, int seed, float level = 0.2f)
    {
        var rng = new Random(seed);
        int n = (int)(rate * seconds);
        var x = new float[n];
        double phase = 0, pitch = 120 + rng.NextDouble() * 100;
        // formant resonators (two-pole), centre frequencies drift per syllable
        double[] fc = { 500, 1500, 2500 }, bw = { 90, 110, 160 };
        var y1 = new double[3]; var y2 = new double[3];
        int i = 0;
        bool talking = true;
        while (i < n)
        {
            int len = talking ? (int)(rate * (0.12 + rng.NextDouble() * 0.25)) : (int)(rate * (0.04 + rng.NextDouble() * 0.30) * (rng.NextDouble() < 0.2 ? 3 : 1));
            double target = pitch * (0.85 + rng.NextDouble() * 0.3);
            if (talking)
            {
                fc[0] = 350 + rng.NextDouble() * 500; fc[1] = 900 + rng.NextDouble() * 1400; fc[2] = 2200 + rng.NextDouble() * 800;
            }
            for (int k = 0; k < len && i < n; k++, i++)
            {
                if (!talking) { x[i] = 0; continue; }
                pitch += (target - pitch) * 0.0005;
                phase += 2 * Math.PI * pitch / rate;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                // glottal-like pulse train with a little breath noise
                double src = Math.Pow(Math.Max(0, Math.Sin(phase)), 3) * 2 - 0.5 + (rng.NextDouble() - 0.5) * 0.15;
                double env = Math.Sin(Math.PI * k / len); // syllable envelope
                double o = 0;
                for (int f = 0; f < 3; f++)
                {
                    double r = Math.Exp(-Math.PI * bw[f] / rate), th = 2 * Math.PI * fc[f] / rate;
                    double a1 = 2 * r * Math.Cos(th), a2 = -r * r;
                    double y = (1 - r) * src + a1 * y1[f] + a2 * y2[f];
                    y2[f] = y1[f]; y1[f] = y;
                    o += y / (f + 1);
                }
                x[i] = (float)(o * env);
            }
            talking = !talking;
        }
        Normalize(x, level);
        return x;
    }

    /// <summary>Band-limited noise with a slow envelope (the original, easy test signal).</summary>
    public static float[] SlowNoise(int rate, double seconds, int seed, float level = 0.3f)
    {
        var rng = new Random(seed);
        int n = (int)(rate * seconds);
        var x = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            lp = 0.7f * lp + 0.3f * (float)(rng.NextDouble() * 2 - 1);
            x[i] = lp * (0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 3 * i / rate));
        }
        Normalize(x, level);
        return x;
    }

    /// <summary>Scales a signal so that its RMS over the non-silent part equals <paramref name="rms"/>.</summary>
    public static void Normalize(float[] x, float rms)
    {
        double s = 0; int c = 0;
        foreach (var v in x) if (Math.Abs(v) > 1e-4) { s += (double)v * v; c++; }
        if (c == 0) return;
        float g = (float)(rms / Math.Sqrt(s / c));
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    public static float[] StationaryNoise(int rate, double seconds, int seed, float rms)
    {
        var rng = new Random(seed);
        var x = new float[(int)(rate * seconds)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        Normalize(x, rms);
        return x;
    }

    /// <summary>Low-frequency fan hum: a few harmonics of 100 Hz plus rumble.</summary>
    public static float[] FanHum(int rate, double seconds, int seed, float rms)
    {
        var rng = new Random(seed);
        var x = new float[(int)(rate * seconds)];
        float lp = 0;
        for (int i = 0; i < x.Length; i++)
        {
            lp = 0.98f * lp + 0.02f * (float)(rng.NextDouble() * 2 - 1);
            x[i] = (float)(Math.Sin(2 * Math.PI * 100 * i / rate) + 0.5 * Math.Sin(2 * Math.PI * 200 * i / rate) + 0.3 * Math.Sin(2 * Math.PI * 300 * i / rate)) + lp * 6;
        }
        Normalize(x, rms);
        return x;
    }

    /// <summary>Keyboard-like clicks: very short broadband bursts at random times.</summary>
    public static float[] Keyboard(int rate, double seconds, int seed, float peak)
    {
        var rng = new Random(seed);
        var x = new float[(int)(rate * seconds)];
        int i = (int)(rate * 0.3);
        while (i < x.Length)
        {
            int len = rate / 200; // 5 ms
            for (int k = 0; k < len && i + k < x.Length; k++)
                x[i + k] = (float)((rng.NextDouble() * 2 - 1) * peak * Math.Exp(-k * 6.0 / len));
            i += (int)(rate * (0.08 + rng.NextDouble() * 0.25));
        }
        return x;
    }

    /// <summary>Several overlapping talkers far in the background, as in an open office.</summary>
    public static float[] Babble(int rate, double seconds, int seed, float rms)
    {
        var sum = new float[(int)(rate * seconds)];
        for (int t = 0; t < 4; t++)
        {
            var s = Speech(rate, seconds, seed * 7 + t, 0.1f);
            for (int i = 0; i < sum.Length; i++) sum[i] += s[i];
        }
        Normalize(sum, rms);
        return sum;
    }

    /// <summary>A room-like echo path: <paramref name="delayMs"/> of bulk delay (device and driver buffering, propagation), then a decaying random reverb of <paramref name="tailMs"/>.</summary>
    public static (float[] h, int delaySamples) Path(int rate, int delayMs, int tailMs = 40, double decayMs = 12, float gain = 0.5f, int seed = 99)
    {
        var rng = new Random(seed);
        int delay = rate * delayMs / 1000, tail = rate * tailMs / 1000;
        var h = new float[tail];
        // a strong direct path first, then the diffuse tail
        h[0] = gain;
        for (int k = 1; k < tail; k++) h[k] = (float)((rng.NextDouble() * 2 - 1) * gain * 0.4 * Math.Exp(-k / (rate * decayMs / 1000)));
        return (h, delay);
    }

    public static float[] Convolve(float[] far, float[] h, int delay)
    {
        var echo = new float[far.Length];
        for (int i = 0; i < far.Length; i++)
        {
            float acc = 0;
            for (int k = 0; k < h.Length; k++)
            {
                int j = i - delay - k;
                if (j < 0) break;
                acc += h[k] * far[j];
            }
            echo[i] = acc;
        }
        return echo;
    }

    public static float[] Mix(params float[][] parts)
    {
        var o = new float[parts.Max(p => p.Length)];
        foreach (var p in parts) for (int i = 0; i < p.Length; i++) o[i] += p[i];
        return o;
    }

    public static float[] Delay(float[] x, int samples)
    {
        var o = new float[x.Length];
        for (int i = samples; i < x.Length; i++) o[i] = x[i - samples];
        return o;
    }

    public static float[] Scale(float[] x, float g)
    {
        var o = new float[x.Length];
        for (int i = 0; i < x.Length; i++) o[i] = x[i] * g;
        return o;
    }

    /// <summary>Gates a signal on only inside [fromSec, toSec).</summary>
    public static float[] Window(float[] x, int rate, double fromSec, double toSec)
    {
        var o = new float[x.Length];
        for (int i = (int)(fromSec * rate); i < Math.Min(x.Length, (int)(toSec * rate)); i++) o[i] = x[i];
        return o;
    }
}
