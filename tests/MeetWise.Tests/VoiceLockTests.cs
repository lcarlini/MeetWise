using MeetWise.Speech;

namespace MeetWise.Tests;

public class VoiceLockTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");
    private static string Vad => Path.Combine(Models, "vad", "silero_vad.onnx");
    private static string Emb => Path.Combine(Models, "speaker", "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
    private static string Vox => Path.Combine(Models, "enhance", "sherpa-onnx-voxlock-int8");
    private static string Wavs => Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs");
    private static bool Have => File.Exists(Vad) && File.Exists(Emb) && Directory.Exists(Vox) && Directory.Exists(Wavs);

    private static float[] Wav(string name) => MeetWise.Audio.AudioFile.ReadMono16k(Path.Combine(Wavs, name));
    private static float[] Slice(float[] x, double a, double b) => x[(int)(a * 16000)..(int)(b * 16000)];

    private static double Rms(float[] x, int a, int b)
    {
        double s = 0;
        int n = 0;
        for (int i = a; i < b && i < x.Length; i++, n++) s += x[i] * x[i];
        return Math.Sqrt(s / Math.Max(1, n));
    }

    private static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-9));

    // ---- training quality checks ----

    [Fact]
    public void Quality_AcceptsCleanSpeech()
    {
        if (!Have) return;
        var q = VoiceTrainer.Analyze(Slice(Wav("1.wav"), 0, 6), Vad);
        Assert.True(q.Ok, q.Message);
        Assert.InRange(q.SpeechSeconds, 3, 6);
    }

    [Fact]
    public void Quality_RejectsTooShort_Silence_Clipping_Quiet_AndNoisy()
    {
        if (!Have) return;
        var clean = Slice(Wav("1.wav"), 0, 6);

        var shortClip = VoiceTrainer.Analyze(Slice(Wav("1.wav"), 0, 0.5), Vad);
        Assert.False(shortClip.Ok);
        Assert.Contains("too short", shortClip.Message);

        var silence = VoiceTrainer.Analyze(new float[16000 * 5], Vad);
        Assert.False(silence.Ok);
        Assert.Contains("speech", silence.Message);

        var clipped = VoiceTrainer.Analyze(clean.Select(v => Math.Clamp(v * 40f, -1f, 1f)).ToArray(), Vad);
        Assert.False(clipped.Ok);
        Assert.Contains("distorted", clipped.Message);

        var quiet = VoiceTrainer.Analyze(clean.Select(v => v * 0.01f).ToArray(), Vad);
        Assert.False(quiet.Ok);
        Assert.Contains("quiet", quiet.Message);

        var rng = new Random(3);
        double speechRms = Rms(clean, 0, clean.Length);
        var noisy = VoiceTrainer.Analyze(clean.Select(v => v + (float)((rng.NextDouble() * 2 - 1) * speechRms * 1.2)).ToArray(), Vad);
        Assert.False(noisy.Ok);
        Assert.Contains("noise", noisy.Message);
    }

    [Fact]
    public void Consistency_FlagsADifferentVoice_ButAcceptsTheSameOne()
    {
        if (!Have) return;
        using var emb = new SpeakerEmbedder(Emb);
        var me = Wav("1.wav");
        var mine = new List<float[]> { emb.Embed(Slice(me, 0, 4)), emb.Embed(Slice(me, 4, 8)) };
        Assert.Null(VoiceTrainer.CheckConsistency(emb.Embed(Slice(me, 8, 12)), mine));
        var other = Wav("0.wav");
        var msg = VoiceTrainer.CheckConsistency(emb.Embed(Slice(other, 0, 6)), mine);
        Assert.NotNull(msg);
        Assert.Contains("different voice", msg);
    }

    [Fact]
    public void Build_WritesReferenceClip_Profile_AndReloads()
    {
        if (!Have) return;
        using var emb = new SpeakerEmbedder(Emb);
        var me = Wav("1.wav");
        var dir = Path.Combine(Path.GetTempPath(), "mw-vp-" + Guid.NewGuid().ToString("N"));
        var profile = VoiceTrainer.Build(new[] { Slice(me, 0, 6), Slice(me, 6, 12) }, Vad, emb, "Leandro", dir);

        Assert.True(VoiceProfile.IsTrained(dir));
        var clipSeconds = MeetWise.Audio.AudioFile.ReadMono16k(VoiceProfile.ClipPath(dir)).Length / 16000.0;
        Assert.InRange(clipSeconds, 3.5, 5.2);
        Assert.InRange(profile.SpeechLevelDb, -45, -15);
        var loaded = VoiceProfile.Load(dir)!;
        Assert.Equal("Leandro", loaded.Name);
        Assert.Equal(emb.Dim, loaded.MeanEmbedding.Length);
        Assert.Equal(2, loaded.Sentences);
        VoiceProfile.Delete(dir);
        Assert.False(VoiceProfile.IsTrained(dir));
    }

    // ---- voice lock on a mixed timeline: 0-4 s other only, 4-9 s me only, 9-13 s both, 13-17 s other only ----

    private sealed record Run(float[] Mix, float[] Out);

    private static Run RunLock()
    {
        using var emb = new SpeakerEmbedder(Emb);
        var me = Wav("1.wav");
        var other = Wav("0.wav");
        var dir = Path.Combine(Path.GetTempPath(), "mw-lock-" + Guid.NewGuid().ToString("N"));
        VoiceTrainer.Build(new[] { Slice(me, 0, 6), Slice(me, 6, 8) }, Vad, emb, "Me", dir);

        int n = 17 * 16000;
        var tgt = new float[n];
        var itf = new float[n];
        var speech = me[(8 * 16000)..];
        for (int i = 0; i < 9 * 16000 && i < speech.Length; i++) tgt[4 * 16000 + i] = speech[i];
        for (int i = 0; i < n; i++) itf[i] = other[i % other.Length];
        for (int i = 4 * 16000; i < 9 * 16000; i++) itf[i] = 0;
        var mix = new float[n];
        for (int i = 0; i < n; i++) mix[i] = tgt[i] + itf[i];

        var opts = new EnhancementOptions(EnhancerKind.VoiceIsolation, null, false, Vox, VoiceProfile.ClipPath(dir), 2);
        using var engine = new EnhancementEngine(16000, opts);
        var output = new List<float>();
        for (int i = 0; i < n; i += 160) output.AddRange(engine.Process(mix.AsSpan(i, Math.Min(160, n - i)).ToArray()));
        return new Run(mix, output.ToArray());
    }

    [Fact]
    public void VoiceLock_PassesMe_AndRemovesOtherSpeakers()
    {
        if (!Have) return;
        var r = RunLock();
        double Delta(int a, int b) => Db(Rms(r.Out, a * 16000, b * 16000)) - Db(Rms(r.Mix, a * 16000, b * 16000));
        Assert.True(Delta(13, 17) < -20, $"other speaker not removed after warm-up: {Delta(13, 17):0.0} dB");
        Assert.True(Delta(0, 4) < -12, $"other speaker leaks at the start: {Delta(0, 4):0.0} dB");
        Assert.InRange(Delta(4, 9), -2.5, 2.5); // my voice passes at about the same level
        // While both talk, the output must stay near my level alone (the other voice removed)
        double mine = Db(Rms(r.Out, 4 * 16000, 9 * 16000));
        Assert.InRange(Db(Rms(r.Out, 9 * 16000, 13 * 16000)), mine - 8, mine + 4);
    }
}
