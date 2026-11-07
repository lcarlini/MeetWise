using System.Text.Json;

namespace MeetWise.Speech;

public sealed record ClipQuality(bool Ok, string Message, double SpeechSeconds, double PeakDb, double LevelDb, double SnrDb);

/// <summary>What MeetWise knows about the user's voice: a reference clip for voice isolation and a voiceprint for naming the user.</summary>
public sealed class VoiceProfile
{
    public string Name { get; set; } = "Me";
    public float[] MeanEmbedding { get; set; } = Array.Empty<float>();

    /// <summary>Typical speech level of the user in dBFS (RMS over speech), used to calibrate the strict gate.</summary>
    public double SpeechLevelDb { get; set; }
    public int Sentences { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static string DefaultDirectory => Path.Combine(MeetWise.Core.AppPaths.Roaming, "voice");
    public static string ClipPath(string? dir = null) => Path.Combine(dir ?? DefaultDirectory, "me.wav");
    private static string JsonPath(string dir) => Path.Combine(dir, "profile.json");

    public bool Exists(string? dir = null) => File.Exists(ClipPath(dir));

    public static bool IsTrained(string? dir = null) => File.Exists(ClipPath(dir)) && File.Exists(JsonPath(dir ?? DefaultDirectory));

    public void Save(string? dir = null)
    {
        dir ??= DefaultDirectory;
        Directory.CreateDirectory(dir);
        File.WriteAllText(JsonPath(dir), JsonSerializer.Serialize(this));
    }

    public static VoiceProfile? Load(string? dir = null)
    {
        dir ??= DefaultDirectory;
        try { return File.Exists(JsonPath(dir)) ? JsonSerializer.Deserialize<VoiceProfile>(File.ReadAllText(JsonPath(dir))) : null; }
        catch (JsonException) { return null; }
    }

    public static void Delete(string? dir = null)
    {
        dir ??= DefaultDirectory;
        foreach (var f in new[] { ClipPath(dir), JsonPath(dir) })
            if (File.Exists(f)) File.Delete(f);
    }
}

/// <summary>Guided voice training: checks each recorded sentence and builds the profile from the good ones.</summary>
public static class VoiceTrainer
{
    public const double MinSpeechSeconds = 2.5;
    public const double MaxPeakDb = -1.0;
    public const double MinLevelDb = -35.0;
    public const double MinSnrDb = 15.0;
    /// <summary>Same-person 3 s voiceprints scored 0.78-0.94 against each other; a clearly lower score means someone else spoke.</summary>
    public const float MinConsistency = 0.65f;

    /// <summary>The sentences the user reads aloud. Varied sounds, each about 4-6 seconds.</summary>
    public static readonly string[] Prompts =
    {
        "The quick brown fox jumps over the lazy dog near the quiet river.",
        "Please schedule our weekly review for Thursday afternoon, around three o'clock.",
        "I would like to confirm the budget, the timeline, and who owns each task.",
        "Yesterday we shipped version two point five and fixed nineteen small problems.",
        "Could you share the numbers from last quarter before we start the discussion?",
        "Thank you everyone, let's summarize the decisions and agree on the next steps."
    };

    public static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));

    public static ClipQuality Analyze(float[] samples16k, string vadModel)
    {
        if (samples16k.Length < 16000) return new ClipQuality(false, "That recording is too short. Read the whole sentence.", 0, -99, -99, 0);

        double peak = 0;
        foreach (var v in samples16k) peak = Math.Max(peak, Math.Abs(v));
        double peakDb = Db(peak);

        var segments = SpeechDetector.Segments(samples16k, vadModel);
        double speechSeconds = segments.Sum(s => s.End - s.Start);
        double levelDb = SpeechLevelDb(samples16k, segments);
        double snrDb = levelDb - NoiseFloorDb(samples16k);

        if (speechSeconds < MinSpeechSeconds) return new ClipQuality(false, $"I only heard {speechSeconds:0.0} s of speech. Read the full sentence clearly.", speechSeconds, peakDb, levelDb, snrDb);
        if (peakDb > MaxPeakDb) return new ClipQuality(false, "The recording is distorted (too loud). Move back from the microphone or lower its volume.", speechSeconds, peakDb, levelDb, snrDb);
        if (levelDb < MinLevelDb) return new ClipQuality(false, "Your voice is too quiet. Move closer to the microphone or raise its volume.", speechSeconds, peakDb, levelDb, snrDb);
        if (snrDb < MinSnrDb) return new ClipQuality(false, "There is too much background noise. Find a quieter place and try again.", speechSeconds, peakDb, levelDb, snrDb);
        return new ClipQuality(true, "Good recording.", speechSeconds, peakDb, levelDb, snrDb);
    }

    /// <summary>Compares a new sentence with the previously accepted ones. Null when they sound like the same person.</summary>
    public static string? CheckConsistency(float[] newEmbedding, IReadOnlyList<float[]> acceptedEmbeddings)
    {
        if (acceptedEmbeddings.Count == 0) return null;
        float score = SpeakerEmbedder.Cosine(newEmbedding, SpeakerEmbedder.Mean(acceptedEmbeddings));
        return score >= MinConsistency ? null : $"This sounds like a different voice (match {score:0.00}). Only you should speak during training.";
    }

    public static double SpeechLevelDb(float[] samples, IReadOnlyList<(double Start, double End)> segments)
    {
        double sum = 0; long n = 0;
        foreach (var (s, e) in segments)
        {
            int a = Math.Max(0, (int)(s * 16000)), b = Math.Min(samples.Length, (int)(e * 16000));
            for (int i = a; i < b; i++) { sum += samples[i] * samples[i]; n++; }
        }
        return n == 0 ? -99 : Db(Math.Sqrt(sum / n));
    }

    /// <summary>Background level: the quietest tenth of 20 ms frames.</summary>
    public static double NoiseFloorDb(float[] samples)
    {
        const int frame = 320;
        var levels = new List<double>();
        for (int i = 0; i + frame <= samples.Length; i += frame)
        {
            double s = 0;
            for (int k = 0; k < frame; k++) s += samples[i + k] * samples[i + k];
            levels.Add(Math.Sqrt(s / frame));
        }
        if (levels.Count == 0) return -99;
        levels.Sort();
        return Db(levels.Take(Math.Max(1, levels.Count / 10)).Average());
    }

    /// <summary>Builds the profile from accepted sentences and writes the reference clip (about 5 s of the cleanest speech).</summary>
    public static VoiceProfile Build(IReadOnlyList<float[]> sentences, string vadModel, SpeakerEmbedder embedder, string name,
        string? directory = null, double clipSeconds = 5.0)
    {
        if (sentences.Count == 0) throw new ArgumentException("No recordings to build a profile from.", nameof(sentences));
        var embeddings = sentences.Select(embedder.Embed).ToList();

        // Pick speech from the cleanest sentences until the reference clip is long enough.
        var ranked = sentences.Select((s, i) => (s, q: Analyze(s, vadModel))).OrderByDescending(x => x.q.SnrDb).ToList();
        var clip = new List<float>();
        foreach (var (s, _) in ranked)
        {
            foreach (var (a, b) in SpeechDetector.Segments(s, vadModel))
            {
                int from = Math.Max(0, (int)(a * 16000)), to = Math.Min(s.Length, (int)(b * 16000));
                clip.AddRange(s.AsSpan(from, to - from).ToArray());
                if (clip.Count >= clipSeconds * 16000) break;
            }
            if (clip.Count >= clipSeconds * 16000) break;
        }
        var wanted = (int)(clipSeconds * 16000);
        if (clip.Count > wanted) clip.RemoveRange(wanted, clip.Count - wanted);

        var allSegments = sentences.SelectMany(s => SpeechDetector.Segments(s, vadModel).Select(seg => (s, seg))).ToList();
        double level = Db(Math.Sqrt(allSegments.Average(x =>
        {
            int a = Math.Max(0, (int)(x.seg.Start * 16000)), b = Math.Min(x.s.Length, (int)(x.seg.End * 16000));
            double sum = 0;
            for (int i = a; i < b; i++) sum += x.s[i] * x.s[i];
            return b > a ? sum / (b - a) : 0;
        })));

        var dir = directory ?? VoiceProfile.DefaultDirectory;
        Directory.CreateDirectory(dir);
        using (var w = new NAudio.Wave.WaveFileWriter(VoiceProfile.ClipPath(dir), new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            var arr = clip.ToArray();
            w.WriteSamples(arr, 0, arr.Length);
        }
        var profile = new VoiceProfile
        {
            Name = name,
            MeanEmbedding = SpeakerEmbedder.Mean(embeddings),
            SpeechLevelDb = level,
            Sentences = sentences.Count
        };
        profile.Save(dir);
        return profile;
    }
}
