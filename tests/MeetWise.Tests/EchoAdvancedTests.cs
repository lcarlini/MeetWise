using MeetWise.Audio;
using MeetWise.Speech;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MeetWise.Tests;

public class EchoAdvancedTests
{
    private const int Rate = 16000;
    private readonly ITestOutputHelper _out;
    public EchoAdvancedTests(ITestOutputHelper o) => _out = o;

    private static float[] Run(EchoCanceller aec, float[] mic, float[] far, int block = 160) => EchoRobustnessTests.Run(aec, mic, far, block);

    private static double Erle(float[] echo, float[] outp, double from, double to, int rate = Rate)
    {
        int a = (int)(from * rate), n = (int)((to - from) * rate);
        return EchoSim.Db(EchoSim.Rms(echo.AsSpan(a, n)) / Math.Max(EchoSim.Rms(outp.AsSpan(a, n)), 1e-9));
    }

    // ---- delay estimation ----

    [Theory]
    [InlineData(16000, 35)]
    [InlineData(16000, 333)]
    [InlineData(44100, 120)]
    [InlineData(48000, 35)]
    [InlineData(48000, 250)]
    public void DelayEstimator_FindsTheDelay_AtCommonSampleRates(int rate, int delayMs)
    {
        var far = EchoSim.Speech(rate, 6, 21);
        var (h, d) = EchoSim.Path(rate, delayMs);
        var echo = EchoSim.Convolve(far, h, d);
        var est = new DelayEstimator(rate);
        for (int i = 0; i + rate / 100 <= far.Length; i += rate / 100) est.Add(echo.AsSpan(i, rate / 100), far.AsSpan(i, rate / 100));
        Assert.True(est.Locked, "no lock");
        _out.WriteLine($"{rate} Hz, true {delayMs} ms: estimated {est.DelayMs:0.0} ms, confidence {est.Confidence:0}");
        Assert.InRange(est.DelayMs, delayMs - 8, delayMs + 12); // decimation and windowing put the peak a few ms late
    }



    [Fact]
    public void DelayEstimator_DoesNotLock_WhenTheMicrophoneHasNoEcho()
    {
        // headphones: the far end plays but the microphone never hears it (here the microphone is room noise)
        var far = EchoSim.Speech(Rate, 12, 22);
        var mic = EchoSim.StationaryNoise(Rate, 12, 23, 0.05f);
        var est = new DelayEstimator(Rate);
        for (int i = 0; i + 160 <= far.Length; i += 160) est.Add(mic.AsSpan(i, 160), far.AsSpan(i, 160));
        Assert.False(est.Locked);
        Assert.Equal(0, est.DelaySamples);
    }

    [Fact]
    public void DelayEstimator_Unlock_ClearsTheLock_AndAsksForStrongerEvidence()
    {
        var far = EchoSim.Speech(Rate, 6, 29);
        var (h, d) = EchoSim.Path(Rate, 90);
        var echo = EchoSim.Convolve(far, h, d);
        var est = new DelayEstimator(Rate);
        for (int i = 0; i + 160 <= far.Length; i += 160) est.Add(echo.AsSpan(i, 160), far.AsSpan(i, 160));
        Assert.True(est.Locked);
        int version = est.Version;
        double before = est.RequiredRatio;
        est.Unlock();
        Assert.False(est.Locked);
        Assert.Equal(0, est.DelaySamples);
        Assert.True(est.Version > version);
        Assert.True(est.RequiredRatio > before);
        est.Reset();
        Assert.Equal(6, est.RequiredRatio);
    }

    [Fact]
    public void DelayEstimator_SurvivesLoudNoiseAndNearEndTalking()
    {
        var far = EchoSim.Speech(Rate, 12, 24);
        var (h, d) = EchoSim.Path(Rate, 150);
        var echo = EchoSim.Convolve(far, h, d);
        var near = EchoSim.Speech(Rate, 12, 25, 0.15f);
        var noise = EchoSim.StationaryNoise(Rate, 12, 26, 0.03f);
        var mic = EchoSim.Mix(echo, near, noise);
        var est = new DelayEstimator(Rate);
        for (int i = 0; i + 160 <= mic.Length; i += 160) est.Add(mic.AsSpan(i, 160), far.AsSpan(i, 160));
        Assert.True(est.Locked);
        Assert.InRange(est.DelayMs, 142, 158);
    }

    [Fact]
    public void DelayEstimator_FollowsALaterChangeOfDelay()
    {
        // e.g. a Bluetooth speaker connects mid-call and adds 180 ms
        var far = EchoSim.Speech(Rate, 24, 27);
        var (h1, d1) = EchoSim.Path(Rate, 40);
        var (h2, d2) = EchoSim.Path(Rate, 220);
        var e1 = EchoSim.Convolve(far, h1, d1);
        var e2 = EchoSim.Convolve(far, h2, d2);
        var mic = new float[far.Length];
        for (int i = 0; i < mic.Length; i++) mic[i] = i < Rate * 12 ? e1[i] : e2[i];
        var est = new DelayEstimator(Rate);
        double atTen = 0;
        for (int i = 0; i + 160 <= mic.Length; i += 160)
        {
            est.Add(mic.AsSpan(i, 160), far.AsSpan(i, 160));
            if (i == Rate * 10) atTen = est.DelayMs;
        }
        _out.WriteLine($"before {atTen:0} ms, after {est.DelayMs:0} ms");
        Assert.InRange(atTen, 32, 48);
        Assert.InRange(est.DelayMs, 212, 228);
    }

    [Fact]
    public async Task DelayEstimator_InBackgroundMode_ReachesTheSameAnswer()
    {
        var far = EchoSim.Speech(Rate, 8, 28);
        var (h, d) = EchoSim.Path(Rate, 200);
        var echo = EchoSim.Convolve(far, h, d);
        var est = new DelayEstimator(Rate, background: true);
        for (int i = 0; i + 160 <= far.Length; i += 160)
        {
            est.Add(echo.AsSpan(i, 160), far.AsSpan(i, 160));
            // the test feeds audio much faster than real time, so let each estimate finish as it would between real hops
            while (est.IsEstimating) await Task.Delay(1);
        }
        for (int w = 0; w < 100 && !est.Locked; w++) await Task.Delay(50);
        _out.WriteLine($"delay {est.DelayMs:0.0} ms");
        Assert.True(est.Locked);
        Assert.InRange(est.DelayMs, 192, 208); // same answer as the inline mode; what it costs the audio thread is measured in EchoPerformanceTests
    }

    // ---- behaviour of the whole canceller ----

    [Fact]
    public void Headphones_NoEcho_LeavesTheMicrophoneAlone()
    {
        var far = EchoSim.Speech(Rate, 12, 31);
        var near = EchoSim.Speech(Rate, 12, 32, 0.15f);
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, near, far);
        double change = EchoSim.Db(EchoSim.Rms(outp.AsSpan(Rate * 4)) / EchoSim.Rms(near.AsSpan(Rate * 4)));
        _out.WriteLine($"level change {change:0.00} dB; delay locked: {aec.Stats.DelayLocked}");
        // Two unrelated talkers can line up by chance and the estimator may lock on that; the filter then has nothing to cancel, and a lock that
        // cancels nothing is withdrawn after six seconds. What matters to the user: their voice keeps its level.
        Assert.InRange(change, -0.7, 0.3);
    }

    [Fact]
    public void Reset_ForgetsTheLearnedPath()
    {
        var far = EchoSim.Speech(Rate, 20, 33);
        var (h, d) = EchoSim.Path(Rate, 120);
        var echo = EchoSim.Convolve(far, h, d);
        var aec = new EchoCanceller(Rate);
        var first = Run(aec, echo, far);
        Assert.True(Erle(echo, first, 13, 20) >= 12);
        Assert.True(aec.Stats.DelayLocked);

        aec.Reset();
        Assert.False(aec.Stats.DelayLocked);
        Assert.Equal(0, aec.DelaySamples);
        var again = Run(aec, echo.AsSpan(Rate * 13).ToArray(), far.AsSpan(Rate * 13).ToArray());
        double justAfter = Erle(echo.AsSpan(Rate * 13).ToArray(), again, 0, 1);
        _out.WriteLine($"ERLE in the first second after reset: {justAfter:0.0} dB");
        Assert.True(justAfter < 6, "a reset canceller must not still be cancelling with stale coefficients");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(160)]
    [InlineData(320)]
    [InlineData(480)]
    public void TheBlockSizeOfTheAudioCallback_DoesNotMatter(int block)
    {
        // WASAPI delivers 10 ms or 20 ms blocks (and occasionally odd ones); results must not depend on it
        var far = EchoSim.Speech(Rate, 16, 34);
        var (h, d) = EchoSim.Path(Rate, 90);
        var echo = EchoSim.Convolve(far, h, d);
        var outp = Run(new EchoCanceller(Rate), echo, far, block);
        double erle = Erle(echo, outp, 11, 16);
        _out.WriteLine($"block {block}: ERLE {erle:0.0} dB");
        Assert.True(erle >= 12);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void WorksAtEverySampleRateTheAppCanSee(int rate)
    {
        var far = EchoSim.Speech(rate, 12, 35);
        var (h, d) = EchoSim.Path(rate, 80);
        var echo = EchoSim.Convolve(far, h, d);
        var outp = Run(new EchoCanceller(rate), echo, far, rate / 100);
        double erle = Erle(echo, outp, 8, 12, rate);
        _out.WriteLine($"{rate} Hz: ERLE {erle:0.0} dB");
        Assert.True(erle >= 12, $"{rate} Hz: ERLE only {erle:0.0} dB");
    }

    [Fact]
    public void SeveralApplicationsPlayingAtOnce_AreOneReference()
    {
        // loopback is the mix of everything that plays: a meeting, a video, a notification sound
        var meeting = EchoSim.Speech(Rate, 16, 36);
        var video = EchoSim.Speech(Rate, 16, 37, 0.12f);
        var ping = EchoSim.Keyboard(Rate, 16, 38, 0.2f);
        var far = EchoSim.Mix(meeting, video, ping);
        var (h, d) = EchoSim.Path(Rate, 110);
        var echo = EchoSim.Convolve(far, h, d);
        var outp = Run(new EchoCanceller(Rate), echo, far);
        double erle = Erle(echo, outp, 11, 16);
        _out.WriteLine($"mixed reference: ERLE {erle:0.0} dB");
        Assert.True(erle >= 12);
    }

    [Fact]
    public void AReferenceThatDoesNotMatchTheMicrophone_NeverMakesTheSignalWorse()
    {
        // the meeting app plays on a different device than the one being captured: the "reference" is unrelated to the echo
        var farHeard = EchoSim.Speech(Rate, 16, 39);
        var (h, d) = EchoSim.Path(Rate, 70);
        var echo = EchoSim.Convolve(farHeard, h, d);
        var near = EchoSim.Speech(Rate, 16, 40, 0.1f);
        var mic = EchoSim.Mix(echo, near);
        var wrongRef = EchoSim.Speech(Rate, 16, 41);
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, mic, wrongRef);
        double change = EchoSim.Db(EchoSim.Rms(outp.AsSpan(Rate * 4)) / EchoSim.Rms(mic.AsSpan(Rate * 4)));
        _out.WriteLine($"output vs microphone: {change:0.00} dB, resets {aec.Stats.Resets}");
        Assert.True(change < 0.5, $"unrelated reference made the output {change:0.0} dB louder");
        Assert.All(outp, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void ResidualSuppression_RemovesMoreEcho_AndLeavesTheNearEndTalkerAlone()
    {
        var far = EchoSim.Speech(Rate, 22, 42);
        var (h, d) = EchoSim.Path(Rate, 75, tailMs: 90, decayMs: 30); // a roomy, reverberant path the linear filter cannot fully model
        var echo = EchoSim.Convolve(far, h, d);
        var near = EchoSim.Window(EchoSim.Speech(Rate, 22, 43, 0.25f), Rate, 14, 20);
        var mic = EchoSim.Mix(echo, near);

        var linear = Run(new EchoCanceller(Rate, options: new EchoOptions(ResidualSuppression: false)), mic, far);
        var withNlp = new EchoCanceller(Rate);
        var full = Run(withNlp, mic, far);

        double erleLinear = Erle(echo, linear, 8, 14), erleFull = Erle(echo, full, 8, 14);
        double nearLinear = EchoSim.Rms(linear.AsSpan(Rate * 15, Rate * 4)) / EchoSim.Rms(near.AsSpan(Rate * 15, Rate * 4));
        double nearFull = EchoSim.Rms(full.AsSpan(Rate * 15, Rate * 4)) / EchoSim.Rms(near.AsSpan(Rate * 15, Rate * 4));
        _out.WriteLine($"far-end only: ERLE {erleLinear:0.0} -> {erleFull:0.0} dB; double talk level {EchoSim.Db(nearLinear):0.0} -> {EchoSim.Db(nearFull):0.0} dB re talker");
        Assert.True(erleFull >= erleLinear + 1.5, $"suppressor added only {erleFull - erleLinear:0.0} dB");
        Assert.True(Math.Abs(EchoSim.Db(nearFull) - EchoSim.Db(nearLinear)) < 0.7, "suppressor changed the near-end level");
    }

    [Fact]
    public void ResidualSuppression_DoesNotPump_AndShortTransientsPassThrough()
    {
        var far = EchoSim.Speech(Rate, 20, 44);
        var (h, d) = EchoSim.Path(Rate, 75, tailMs: 90, decayMs: 30);
        var echo = EchoSim.Convolve(far, h, d);
        // a laugh-like burst and consonant-like clicks from the near-end talker at 15 s, during far-end speech
        var clicks = EchoSim.Keyboard(Rate, 20, 45, 0.3f);
        var near = EchoSim.Window(clicks, Rate, 15, 19);
        var mic = EchoSim.Mix(echo, near);
        var aec = new EchoCanceller(Rate);
        var outp = new float[mic.Length];
        var gains = new List<double>();
        for (int i = 0; i + 160 <= mic.Length; i += 160)
        {
            aec.Process(mic.AsSpan(i, 160), far.AsSpan(i, 160)).CopyTo(outp, i);
            if (i > Rate * 10 && i < Rate * 14) gains.Add(aec.Stats.ResidualGain);
        }
        double min = gains.Min(), max = gains.Max();
        // gain moves by at most a fraction per 10 ms block: smoothed, so it cannot pump audibly
        double maxStep = 0;
        for (int i = 1; i < gains.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(gains[i] - gains[i - 1]));
        _out.WriteLine($"residual gain {min:0.00}..{max:0.00}, largest 10 ms step {maxStep:0.00}");
        Assert.True(min >= 0.17);
        Assert.True(maxStep < 0.45, $"gain jumped {maxStep:0.00} within 10 ms");

        // clicks that are well above the echo must not be touched (compare peaks)
        double peakNear = 0, peakOut = 0;
        for (int i = Rate * 15; i < Rate * 19; i++) { peakNear = Math.Max(peakNear, Math.Abs(near[i])); peakOut = Math.Max(peakOut, Math.Abs(outp[i])); }
        _out.WriteLine($"click peak in {peakNear:0.00}, out {peakOut:0.00}");
        Assert.True(peakOut >= peakNear * 0.8, "transient was attenuated");
    }

    [Fact]
    public void TheCanceller_AddsNoLatency()
    {
        // an impulse in the microphone comes out at the same sample index while far-end speech is playing
        var far = EchoSim.Speech(Rate, 3, 46);
        var mic = new float[far.Length];
        mic[Rate * 2 + 37] = 0.5f;
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, mic, far, 1);
        int peak = 0;
        for (int i = 0; i < outp.Length; i++) if (Math.Abs(outp[i]) > Math.Abs(outp[peak])) peak = i;
        Assert.Equal(Rate * 2 + 37, peak);
        Assert.Equal(0.5f, outp[peak], 3);
    }

    [Fact]
    public void Stats_ReportWhatTheCancellerHasLearned()
    {
        var far = EchoSim.Speech(Rate, 16, 47);
        var (h, d) = EchoSim.Path(Rate, 140);
        var echo = EchoSim.Convolve(far, h, d);
        var aec = new EchoCanceller(Rate);
        Run(aec, echo, far);
        var st = aec.Stats;
        _out.WriteLine($"delay {st.DelayMs:0.0} ms, ERLE {st.ErleDb:0.0} dB, resets {st.Resets}, frozen {st.FrozenFraction:P0} (geigel {st.GeigelFraction:P0}, error-ratio {st.ErrorRatioFraction:P0})");
        Assert.True(st.DelayLocked);
        Assert.InRange(st.DelayMs, 105, 130); // true 140 ms: measured about 147, minus a quarter of the 100 ms filter (the window is centred on the echo)
        Assert.True(st.ErleDb > 10);
        Assert.Equal(0, st.Resets);
        Assert.True(st.FrozenFraction < 0.2);
    }

    [Fact]
    public void RealSpeech_FarAndNearEnd_Through_TheEnhancementEngine()
    {
        var wavs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs");
        if (!Directory.Exists(wavs)) return; // models are downloaded on first use
        static float[] Load(string path)
        {
            using var r = new WaveFileReader(path);
            var sp = r.ToSampleProvider();
            var all = new List<float>();
            var buf = new float[16000];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
            return all.ToArray();
        }
        var a = Load(Path.Combine(wavs, "0.wav"));
        var b = Load(Path.Combine(wavs, "1.wav"));
        // far end: speaker A looped to 40 s; near end: speaker B talks from 25 s
        var far = new float[Rate * 40];
        for (int i = 0; i < far.Length; i++) far[i] = a[i % a.Length];
        var (h, d) = EchoSim.Path(Rate, 95, tailMs: 50, decayMs: 15);
        var echo = EchoSim.Convolve(far, h, d);
        var near = new float[far.Length];
        for (int i = Rate * 25; i < far.Length; i++) near[i] = b[(i - Rate * 25) % b.Length] * 0.7f;
        var mic = EchoSim.Mix(echo, near);

        using var engine = new EnhancementEngine(Rate, new EnhancementOptions(null, null, EchoCancellation: true, Echo: new EchoOptions()));
        var outp = new float[mic.Length];
        for (int i = 0; i + 160 <= mic.Length; i += 160)
            engine.Process(mic.AsSpan(i, 160).ToArray(), far.AsSpan(i, 160).ToArray()).CopyTo(outp, i);
        double erle = Erle(echo, outp, 15, 24);
        var diff = new float[outp.Length];
        for (int i = 0; i < diff.Length; i++) diff[i] = outp[i] - near[i];
        double nearRms = EchoSim.Rms(near.AsSpan(Rate * 28, Rate * 10));
        double err = EchoSim.Rms(diff.AsSpan(Rate * 28, Rate * 10));
        _out.WriteLine($"real speech: ERLE {erle:0.0} dB (far end alone), near-end error {EchoSim.Db(err / nearRms):0.0} dB re talker during double talk");
        Assert.True(erle >= 10, $"ERLE {erle:0.0} dB");
        Assert.True(err / nearRms < 0.8, "near-end speech was damaged");
    }

    // ---- pipeline interaction ----

    [Fact]
    public void Engine_DoesNotModifyTheReference_AndCleansTheMicrophone()
    {
        var far = EchoSim.Speech(Rate, 14, 51);
        var (h, d) = EchoSim.Path(Rate, 100);
        var echo = EchoSim.Convolve(far, h, d);
        var farCopy = (float[])far.Clone();
        using var engine = new EnhancementEngine(Rate, new EnhancementOptions(null, null, EchoCancellation: true, Echo: new EchoOptions()));
        var outp = new float[echo.Length];
        for (int i = 0; i + 160 <= echo.Length; i += 160)
            engine.Process(echo.AsSpan(i, 160).ToArray(), far.AsSpan(i, 160).ToArray()).CopyTo(outp, i);
        Assert.Equal(farCopy, far);
        Assert.True(Erle(echo, outp, 9, 14) >= 12);
    }

    [Fact]
    public void Engine_ReferenceBlockPassedByTheCaller_IsLeftUntouched()
    {
        // noise suppression and voice isolation run on the microphone only; the reference array the caller passes in must come back unchanged
        using var engine = new EnhancementEngine(Rate, new EnhancementOptions(null, null, EchoCancellation: true, Echo: new EchoOptions()));
        var far = EchoSim.Speech(Rate, 1, 52);
        var keep = (float[])far.AsSpan(0, 160).ToArray().Clone();
        var farBlock = far.AsSpan(0, 160).ToArray();
        engine.Process(EchoSim.Speech(Rate, 1, 53).AsSpan(0, 160).ToArray(), farBlock);
        Assert.Equal(keep, farBlock);
    }

    [Fact]
    public void Engine_WithoutAFarEndSignal_BehavesExactlyLikeNoEchoCancellation_WithNoiseSuppression()
    {
        // regression guard: turning echo cancellation on must not change noise suppression when nothing is playing
        var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "enhance", "gtcrn_simple.onnx");
        if (!File.Exists(model)) return;
        var mic = EchoSim.Mix(EchoSim.Speech(Rate, 6, 54, 0.1f), EchoSim.StationaryNoise(Rate, 6, 55, 0.03f));
        float[] Process(bool echo)
        {
            using var engine = new EnhancementEngine(Rate, new EnhancementOptions(EnhancerKind.Light, model, EchoCancellation: echo));
            var o = new List<float>();
            for (int i = 0; i + 160 <= mic.Length; i += 160) o.AddRange(engine.Process(mic.AsSpan(i, 160).ToArray(), new float[160]));
            return o.ToArray();
        }
        Assert.Equal(Process(false), Process(true));
    }

    [Fact]
    public void Engine_NoiseSuppressionStillWorks_AfterEchoCancellation()
    {
        var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "enhance", "gtcrn_simple.onnx");
        if (!File.Exists(model)) return;
        var far = EchoSim.Speech(Rate, 14, 56);
        var (h, d) = EchoSim.Path(Rate, 85);
        var echo = EchoSim.Convolve(far, h, d);
        var noise = EchoSim.StationaryNoise(Rate, 14, 57, 0.05f);
        var mic = EchoSim.Mix(echo, noise);
        using var engine = new EnhancementEngine(Rate, new EnhancementOptions(EnhancerKind.Light, model, EchoCancellation: true, Echo: new EchoOptions()));
        var outp = new List<float>();
        for (int i = 0; i + 160 <= mic.Length; i += 160) outp.AddRange(engine.Process(mic.AsSpan(i, 160).ToArray(), far.AsSpan(i, 160).ToArray()));
        var o = outp.ToArray();
        double reduction = EchoSim.Db(EchoSim.Rms(mic.AsSpan(Rate * 9, Rate * 4)) / EchoSim.Rms(o.AsSpan(Rate * 9, Rate * 4)));
        _out.WriteLine($"echo + noise reduced by {reduction:0.0} dB");
        Assert.True(reduction >= 10, $"only {reduction:0.0} dB");
        Assert.All(o, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1.01f));
    }
}

public class ReferenceTimelineTests
{
    private sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    private static float[] Ramp(int from, int count) => Enumerable.Range(from, count).Select(i => (float)i).ToArray();

    [Fact]
    public void ContinuousStream_ComesOutOnce_InOrder_ShiftedOnlyByTheJitterMargin()
    {
        var clock = new Clock();
        var t = new ReferenceTimeline(16000, clock: clock.Now, ticksPerSecond: 1000);
        var read = new List<float>();
        var buf = new float[160];
        for (int block = 0; block < 100; block++)
        {
            clock.Ms += 10;
            t.Write(Ramp(1 + block * 160, 160));
            t.Read(buf);
            read.AddRange(buf);
        }
        int margin = 16000 * 60 / 1000;
        for (int i = 0; i < read.Count; i++)
            Assert.Equal(i < margin ? 0f : (float)(i - margin + 1), read[i]);
    }

    [Fact]
    public void SilentSpeakers_DoNotShiftTheReference_WhenPlaybackResumes()
    {
        // WASAPI loopback sends nothing while the speakers are silent: audio that arrives after 1 s of nothing belongs one second later
        var clock = new Clock();
        var t = new ReferenceTimeline(16000, clock: clock.Now, ticksPerSecond: 1000);
        clock.Ms = 10; t.Write(Ramp(1, 160));
        clock.Ms = 1000; t.Write(Ramp(1001, 160));
        var all = new float[17000];
        t.Read(all);
        int margin = 16000 * 60 / 1000;
        Assert.Equal(1f, all[margin]);                        // first burst at the start
        int second = Array.IndexOf(all, 1001f);
        Assert.Equal(16000 - 160 + margin, second);           // second burst at its wall-clock position, not right behind the first
        Assert.True(t.GapSamplesFilled > 15000);
    }

    [Fact]
    public void Start_ForgetsStaleAudio()
    {
        var clock = new Clock();
        var t = new ReferenceTimeline(16000, clock: clock.Now, ticksPerSecond: 1000);
        clock.Ms = 100; t.Write(Ramp(1, 1600));
        t.Start(); // the stream restarted (device change)
        var buf = new float[4000];
        t.Read(buf);
        Assert.All(buf, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void AReaderThatIsAheadOfTheWriter_GetsSilenceNotGarbage_AndIsCounted()
    {
        var clock = new Clock();
        var t = new ReferenceTimeline(16000, clock: clock.Now, ticksPerSecond: 1000);
        var buf = new float[1600];
        t.Read(buf);
        Assert.All(buf, v => Assert.Equal(0f, v));
        Assert.True(t.Underruns >= 1);
    }

    [Fact]
    public async Task ProducerAndConsumerOnDifferentThreads_NeverSeeReorderedOrRepeatedSamples()
    {
        var t = new ReferenceTimeline(16000);
        const int total = 16000 * 6;
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < total; i += 160)
            {
                t.Write(Ramp(i + 1, 160));
                if (i % 3200 == 0) Thread.Sleep(1);
            }
        });
        var seen = new List<float>();
        var buf = new float[160];
        while (!producer.IsCompleted || t.Lag > 0)
        {
            t.Read(buf);
            foreach (var v in buf) if (v != 0f) seen.Add(v);
            Thread.Sleep(1);
        }
        await producer;
        Assert.NotEmpty(seen);
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] > seen[i - 1], $"sample {seen[i]} after {seen[i - 1]}");
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(48000, 16000)]
    [InlineData(16000, 48000)]
    public void Resampler_KeepsToneFrequencyDurationAndLevel(int inRate, int outRate)
    {
        var tone = new float[inRate * 2];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.4f * (float)Math.Sin(2 * Math.PI * 1000 * i / inRate);
        var rs = new ReferenceResampler(inRate, outRate);
        var o = new List<float>();
        for (int i = 0; i + inRate / 100 <= tone.Length; i += inRate / 100) rs.Process(tone.AsSpan(i, inRate / 100), s => o.AddRange(s.ToArray()));
        double seconds = o.Count / (double)outRate;
        Assert.InRange(seconds, 1.95, 2.05);
        var mid = o.Skip(outRate / 2).Take(outRate).ToArray();
        int crossings = 0;
        for (int i = 1; i < mid.Length; i++) if (mid[i - 1] < 0 && mid[i] >= 0) crossings++;
        Assert.InRange(crossings, 990, 1010); // 1 kHz stays 1 kHz
        double rms = Math.Sqrt(mid.Sum(v => (double)v * v) / mid.Length);
        Assert.InRange(rms, 0.4 / Math.Sqrt(2) * 0.9, 0.4 / Math.Sqrt(2) * 1.1);
    }

    [Fact]
    public void Resampler_IsAPassThrough_AtEqualRates()
    {
        var rs = new ReferenceResampler(48000, 48000);
        var x = Ramp(1, 480);
        var o = new List<float>();
        rs.Process(x, s => o.AddRange(s.ToArray()));
        Assert.Equal(x, o.ToArray());
    }

    [Fact]
    public void ARestartedStream_AtAnotherSampleRate_StartsClean()
    {
        // device change: the speakers switch from 44.1 kHz to 48 kHz and the loopback stream is rebuilt. The reference for the (48 kHz) microphone must continue
        // without a copy of the old stream mixed in.
        var clock = new Clock();
        var t = new ReferenceTimeline(48000, clock: clock.Now, ticksPerSecond: 1000);
        var first = new ReferenceResampler(44100, 48000);
        var tone = Enumerable.Range(0, 4410).Select(i => 0.5f * (float)Math.Sin(2 * Math.PI * 500 * i / 44100)).ToArray();
        clock.Ms = 100; first.Process(tone, t.Write);

        t.Start(); // restart
        var second = new ReferenceResampler(48000, 48000);
        var silence = new float[480];
        var buf = new float[48000 / 10];
        t.Read(buf);
        Assert.All(buf, v => Assert.Equal(0f, v)); // nothing of the old stream survives
        clock.Ms = 10; second.Process(silence, t.Write);
    }
}
