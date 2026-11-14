using MeetWise.Audio;
using MeetWise.Core.Settings;
using MeetWise.Speech;

namespace MeetWise.Tests;

/// <summary>The clean-microphone chain added for the microphone-only MeetWise: high-pass, gain, limiter, strength, bypass, cable check.</summary>
public class CleanChainTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    private static float[] Sine(int rate, double hz, double seconds, float amp)
    {
        var x = new float[(int)(rate * seconds)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return x;
    }

    private static double Rms(ReadOnlySpan<float> x) { double s = 0; foreach (var v in x) s += v * v; return Math.Sqrt(s / Math.Max(1, x.Length)); }
    private static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

    [Fact]
    public void HighPass_RemovesRumble_KeepsVoiceBand()
    {
        const int rate = 48000;
        var rumble = Sine(rate, 30, 1, 0.5f);
        var voice = Sine(rate, 300, 1, 0.5f);
        Biquad.HighPass(rate, 80).Process(rumble);
        Biquad.HighPass(rate, 80).Process(voice);
        Assert.True(Db(Rms(rumble.AsSpan(rate / 2)) / 0.3536) < -15, "30 Hz rumble should drop by more than 15 dB");
        Assert.InRange(Db(Rms(voice.AsSpan(rate / 2)) / 0.3536), -0.5, 0.5);
    }

    [Fact]
    public void AutoGain_BringsQuietSpeechUp_AndDoesNotPumpNoiseInPauses()
    {
        const int rate = 16000;
        var agc = new AutoGain(rate);
        // 6 s of quiet "speech" at -36 dBFS rms
        var quiet = LatencyProbe.SyntheticVoice(rate, 6);
        for (int i = 0; i < quiet.Length; i++) quiet[i] *= 0.06f;
        double inDb = Db(Rms(quiet.AsSpan(rate * 5)));
        for (int i = 0; i < quiet.Length; i += 160) agc.Process(quiet.AsSpan(i, Math.Min(160, quiet.Length - i)));
        double outDb = Db(Rms(quiet.AsSpan(rate * 5)));
        Assert.True(outDb - inDb > 10, $"gain only {outDb - inDb:0.0} dB");
        double gainAfterSpeech = agc.GainDb;

        // 3 s of faint room noise afterwards: the gain must hold, not climb
        var noise = new float[rate * 3];
        var rng = new Random(3);
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() - 0.5) * 0.001f;
        for (int i = 0; i < noise.Length; i += 160) agc.Process(noise.AsSpan(i, 160));
        Assert.InRange(agc.GainDb, gainAfterSpeech - 0.5, gainAfterSpeech + 0.5);
    }

    [Fact]
    public void Limiter_NeverExceedsCeiling_AndLeavesNormalLevelsAlone()
    {
        const int rate = 48000;
        var lim = new LookAheadLimiter(rate, ceilingDb: -1);
        var loud = Sine(rate, 200, 0.5, 1.6f);
        lim.Process(loud);
        Assert.True(loud.Max(Math.Abs) <= Math.Pow(10, -1 / 20.0) + 1e-4, $"peak {loud.Max(Math.Abs)}");

        var lim2 = new LookAheadLimiter(rate, ceilingDb: -1);
        var normal = Sine(rate, 200, 0.5, 0.3f);
        var copy = (float[])normal.Clone();
        lim2.Process(normal);
        int d = lim2.LatencySamples;
        for (int i = d + 10; i < normal.Length; i += 97) Assert.Equal(copy[i - d], normal[i], 4);
    }

    [Theory]
    [InlineData(0, 6.0)]
    [InlineData(50, 23.0)]
    [InlineData(99, 39.66)]
    public void Strength_MapsToAnAttenuationLimit(int strength, double expectedDb)
        => Assert.Equal(expectedDb, StrengthMix.AttenuationLimitDb(strength), 1);

    [Fact]
    public void Strength100_IsUnlimited() => Assert.True(double.IsPositiveInfinity(StrengthMix.AttenuationLimitDb(100)));

    [Fact]
    public void LatencyProbe_FindsTheFullBandModelDelay_AndStrengthMixIsAligned()
    {
        var model = Path.Combine(Models, "enhance", "dpdfnet2_48khz_hr.onnx");
        if (!File.Exists(model)) return;
        var lat = LatencyProbe.Measure(EnhancerKind.FullBand, model);
        Assert.NotNull(lat);
        Assert.InRange(lat!.Value, 0, 48000 / 5);

        // with strength 0 the output keeps most of the (clean, synthetic) voice: dry and wet are aligned, so no comb-filter loss
        const int rate = 48000;
        var voice = LatencyProbe.SyntheticVoice(rate, 3);
        float[] RunAt(int strength)
        {
            using var e = new EnhancementEngine(rate, new EnhancementOptions(EnhancerKind.FullBand, model, false, NoiseStrength: strength));
            var all = new List<float>();
            for (int i = 0; i + 480 <= voice.Length; i += 480) all.AddRange(e.Process(voice[i..(i + 480)]));
            return all.ToArray();
        }
        var full = RunAt(100);
        var mixed = RunAt(0);
        double fullDb = Db(Rms(full.AsSpan(rate)));
        double dryDb = Db(Rms(voice.AsSpan(rate)));
        double mixedDb = Db(Rms(mixed.AsSpan(rate)));
        // out = wet + a (dry - wet): aligned, its level lies between the model output and the original. Misaligned, the two cancel in places
        // (comb filtering) and the level falls below both.
        Assert.InRange(mixedDb, Math.Min(fullDb, dryDb) - 1, Math.Max(fullDb, dryDb) + 1);
    }

    [Fact]
    public void Engine_PassesAudioThrough_WhenTheModelFails()
    {
        var model = Path.Combine(Models, "enhance", "dpdfnet2_48khz_hr.onnx");
        if (!File.Exists(model)) return;
        using var e = new EnhancementEngine(48000, new EnhancementOptions(EnhancerKind.FullBand, model, false));
        var voice = LatencyProbe.SyntheticVoice(48000, 0.2);
        e.Process(voice[..480]);
        e.FailNextModelCall = true;
        var after = e.Process(voice[480..960]);
        Assert.Equal(480, after.Length);                         // the block came out (unprocessed) instead of silence
        Assert.True(Rms(after) > 0.01);
        Assert.Equal(1, e.Errors);
        Assert.NotEmpty(e.Process(voice[960..1440]).Concat(e.Process(voice[1440..1920])));  // and the model keeps working afterwards
    }

    [Fact]
    public void Engine_ReportsPeaks_ForTheMeters()
    {
        using var e = new EnhancementEngine(16000, new EnhancementOptions(null, null, false));
        e.Process(Sine(16000, 300, 0.1, 0.5f));
        var (i, o) = e.TakePeaks();
        Assert.InRange(i, 0.49f, 0.51f);
        Assert.InRange(o, 0.49f, 0.51f);
        Assert.Equal((0f, 0f), e.TakePeaks());
    }

    [Fact]
    public void CableCheck_FindsTheChirp_InADelayedNoisyRecording()
    {
        const int rate = 48000;
        var chirp = MicService.Chirp(rate, 1.0, 0.25f);
        var rec = new float[rate * 2];
        var rng = new Random(5);
        int delay = 12345;
        for (int i = 0; i < rec.Length; i++) rec[i] = (float)(rng.NextDouble() - 0.5) * 0.02f;
        for (int i = 0; i < chirp.Length; i++) rec[i + delay] += chirp[i];
        var (corr, lag) = MicService.BestCorrelation(chirp, rec);
        Assert.True(corr > 0.9, $"correlation {corr:0.00}");
        Assert.InRange(lag, delay - 1, delay + 1);

        var silence = new float[rate * 2];
        Assert.True(MicService.BestCorrelation(chirp, silence).Correlation < 0.1);
    }

    [Fact]
    public void Settings_FromBeforeVersion2_TurnTheMicrophoneOn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"alwaysOnMicrophone\": false, \"noiseCancellation\": \"Balanced\", \"outputFolder\": \"x\" }");
        var s = new SettingsStore(dir).Load();
        Assert.True(s.AlwaysOnMicrophone);              // an unfed virtual microphone is silent in Zoom
        Assert.Equal(NoiseMode.Balanced, s.NoiseCancellation); // other choices are kept
        Assert.Equal(2, s.SettingsVersion);

        s.AlwaysOnMicrophone = false;                    // after migration, "off" is respected
        new SettingsStore(dir).Save(s);
        Assert.False(new SettingsStore(dir).Load().AlwaysOnMicrophone);
    }

    [Fact]
    public void DisabledCable_IsBroken_AndPicksTheRenamedEndpoint()
    {
        Assert.True(VirtualMic.IsBroken(CableState.Disabled));
        Assert.Equal("mtg.status.disabled", VirtualMic.StatusKey(CableState.Disabled, false, false));
        var eps = new[]
        {
            new EndpointInfo("a", "CABLE Input", "VB-Audio Virtual Cable", NAudio.CoreAudioApi.DataFlow.Render),
            new EndpointInfo("b", "MeetWise Mic Feed", "VB-Audio Virtual Cable", NAudio.CoreAudioApi.DataFlow.Render),
            new EndpointInfo("c", "Speakers", "Realtek", NAudio.CoreAudioApi.DataFlow.Render)
        };
        Assert.Equal("b", VirtualMic.Pick(eps)!.Id);
        Assert.Null(VirtualMic.Pick(eps[2..]));
    }

    [Fact]
    public void StreamResampler_KeepsLengthAndTone()
    {
        var r = new StreamResampler(48000, 16000);
        var x = Sine(48000, 440, 1, 0.5f);
        var y = new List<float>();
        for (int i = 0; i < x.Length; i += 480) y.AddRange(r.Process(x[i..(i + 480)]));
        Assert.InRange(y.Count, 15800, 16100);
        Assert.InRange(Db(Rms(y.ToArray().AsSpan(2000))), Db(0.3536) - 0.5, Db(0.3536) + 0.5);
    }
}
