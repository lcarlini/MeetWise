namespace MeetWise.Speech;

/// <summary>What a processed clip looks like next to its original. All measures are model-free so they work on any clip, any sample rate.</summary>
public sealed record ClipComparison(double OriginalNoiseFloorDb, double ProcessedNoiseFloorDb, double OriginalSpeechDb, double ProcessedSpeechDb)
{
    /// <summary>Positive = quieter background than before (good).</summary>
    public double NoiseRemovedDb => OriginalNoiseFloorDb - ProcessedNoiseFloorDb;

    /// <summary>Near zero = the voice was left alone (good); strongly negative = the voice was damaged or removed.</summary>
    public double VoiceChangeDb => ProcessedSpeechDb - OriginalSpeechDb;

    /// <summary>Speech level above the background after processing.</summary>
    public double ProcessedSnrDb => ProcessedSpeechDb - ProcessedNoiseFloorDb;
    public double OriginalSnrDb => OriginalSpeechDb - OriginalNoiseFloorDb;
}

public static class AudioMetrics
{
    public static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));

    public static double RmsDb(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return -99;
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Db(Math.Sqrt(s / x.Length));
    }

    public static double PeakDb(ReadOnlySpan<float> x)
    {
        float p = 0;
        foreach (var v in x) p = Math.Max(p, Math.Abs(v));
        return Db(p);
    }

    /// <summary>RMS of each 20 ms frame.</summary>
    public static double[] FrameLevels(float[] x, int sampleRate)
    {
        int frame = Math.Max(1, sampleRate / 50);
        int n = x.Length / frame;
        var levels = new double[n];
        for (int f = 0; f < n; f++)
        {
            double s = 0;
            int o = f * frame;
            for (int i = 0; i < frame; i++) s += (double)x[o + i] * x[o + i];
            levels[f] = Math.Sqrt(s / frame);
        }
        return levels;
    }

    /// <summary>Background: mean level of the quietest tenth of frames. Speech: mean level of the loudest third.</summary>
    public static (double NoiseFloorDb, double SpeechDb) Profile(float[] x, int sampleRate)
    {
        var levels = FrameLevels(x, sampleRate);
        if (levels.Length < 10) return (-99, -99);
        var sorted = (double[])levels.Clone();
        Array.Sort(sorted);
        double quiet = sorted.Take(Math.Max(1, sorted.Length / 10)).Average();
        double loud = sorted.Skip(sorted.Length * 2 / 3).Average();
        return (Db(quiet), Db(loud));
    }

    public static ClipComparison Compare(float[] original, int originalRate, float[] processed, int processedRate)
    {
        var (n0, s0) = Profile(original, originalRate);
        var (n1, s1) = Profile(processed, processedRate);
        return new ClipComparison(n0, n1, s0, s1);
    }

    /// <summary>Echo return loss enhancement between a microphone clip and its echo-cancelled version over the same span.</summary>
    public static double ErleDb(ReadOnlySpan<float> before, ReadOnlySpan<float> after)
    {
        int n = Math.Min(before.Length, after.Length);
        if (n == 0) return 0;
        return RmsDb(before[..n]) - RmsDb(after[..n]);
    }

    /// <summary>Word error rate between a reference sentence and what was recognised (0 = perfect). Case, punctuation and extra spaces are ignored.</summary>
    public static double WordErrorRate(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 0 : 1;
        var prev = new int[h.Length + 1];
        var cur = new int[h.Length + 1];
        for (int j = 0; j <= h.Length; j++) prev[j] = j;
        for (int i = 1; i <= r.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= h.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[h.Length] / (double)r.Length;
    }

    private static string[] Words(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray();
        return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Nearest-rank percentile of a list of timings.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
