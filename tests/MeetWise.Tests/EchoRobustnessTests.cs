using MeetWise.Speech;
using Xunit.Abstractions;

namespace MeetWise.Tests;

/// <summary>
/// Echo cancellation on synthetic but realistic signals: speech-like talkers, room-like paths with bulk delay, double talk, noise, and path changes.
/// Every threshold here is a measured, honest bound: see docs in README ("Echo cancellation").
/// </summary>
public class EchoRobustnessTests
{
    private const int Rate = 16000;
    private readonly ITestOutputHelper _out;
    public EchoRobustnessTests(ITestOutputHelper o) => _out = o;

    internal static float[] Run(EchoCanceller aec, float[] mic, float[] far, int block = 160)
    {
        var o = new float[mic.Length];
        for (int i = 0; i + block <= mic.Length; i += block)
            aec.Process(mic.AsSpan(i, block), far.AsSpan(i, block)).CopyTo(o, i);
        return o;
    }

    private static double Erle(float[] echoOnly, float[] output, double fromSec, double toSec, int rate = Rate)
    {
        int a = (int)(fromSec * rate), n = (int)((toSec - fromSec) * rate);
        return EchoSim.Db(EchoSim.Rms(echoOnly.AsSpan(a, n)) / Math.Max(EchoSim.Rms(output.AsSpan(a, n)), 1e-9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(160)]
    [InlineData(250)]
    [InlineData(400)]
    public void Delay_BetweenReferenceAndMicrophone_IsFoundAndCompensated(int delayMs)
    {
        // The reference from the loopback capture is never perfectly aligned with what the microphone hears: driver buffers,
        // Bluetooth and the speaker add anything from a few ms to a few hundred ms.
        var far = EchoSim.Speech(Rate, 16, 1);
        var (h, d) = EchoSim.Path(Rate, delayMs);
        var echo = EchoSim.Convolve(far, h, d);
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, echo, far);
        double erle = Erle(echo, outp, 11, 16);
        _out.WriteLine($"delay {delayMs} ms: ERLE {erle:0.0} dB");
        Assert.True(erle >= 12, $"ERLE only {erle:0.0} dB at {delayMs} ms delay");
    }

    [Fact]
    public void QuietNearEndTalker_IsKept_AndAdaptationResumesAfterDoubleTalk()
    {
        var far = EchoSim.Speech(Rate, 20, 2);
        var (h, d) = EchoSim.Path(Rate, 60);
        var echo = EchoSim.Convolve(far, h, d);
        // near-end talker 10 dB below the echo, speaking from 8 s to 13 s
        var near = EchoSim.Window(EchoSim.Speech(Rate, 20, 77, (float)(EchoSim.Rms(echo.AsSpan(Rate * 2, Rate * 4)) * 0.32)), Rate, 8, 13);
        var mic = EchoSim.Mix(echo, near);
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, mic, far);

        double beforeErle = Erle(echo, outp, 5, 8);
        // near-end level preserved: compare output with the true near-end signal over the double-talk interval
        var diff = new float[outp.Length];
        for (int i = 0; i < diff.Length; i++) diff[i] = outp[i] - near[i];
        double nearRms = EchoSim.Rms(near.AsSpan(Rate * 9, Rate * 3));
        double residual = EchoSim.Rms(diff.AsSpan(Rate * 9, Rate * 3));
        double afterErle = Erle(echo, outp, 16, 20);
        _out.WriteLine($"ERLE before {beforeErle:0.0} dB, near-end error {EchoSim.Db(residual / nearRms):0.0} dB re near-end, ERLE after {afterErle:0.0} dB");
        Assert.True(beforeErle >= 12);
        Assert.True(residual / nearRms < 0.7, $"near-end speech distorted: error is {EchoSim.Db(residual / nearRms):0.0} dB re the talker");
        Assert.True(afterErle >= 12, $"adaptation did not recover after double talk: ERLE {afterErle:0.0} dB");
    }

    [Fact]
    public void LoudNearEndTalker_IsNotAttenuated()
    {
        var far = EchoSim.Speech(Rate, 20, 3);
        var (h, d) = EchoSim.Path(Rate, 80);
        var echo = EchoSim.Convolve(far, h, d);
        var near = EchoSim.Window(EchoSim.Speech(Rate, 20, 78, 0.35f), Rate, 8, 14);
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, EchoSim.Mix(echo, near), far);
        double ratio = EchoSim.Rms(outp.AsSpan(Rate * 9, Rate * 4)) / EchoSim.Rms(near.AsSpan(Rate * 9, Rate * 4));
        _out.WriteLine($"near-end level kept: {EchoSim.Db(ratio):0.0} dB");
        Assert.InRange(EchoSim.Db(ratio), -1.5, 1.5);
    }

    public static IEnumerable<object[]> Noises() => new[]
    {
        new object[] { "stationary" }, new object[] { "keyboard" }, new object[] { "fan" }, new object[] { "babble" }
    };

    [Theory]
    [MemberData(nameof(Noises))]
    public void Noise_DoesNotDestabiliseTheCanceller(string kind)
    {
        var far = EchoSim.Speech(Rate, 16, 4);
        var (h, d) = EchoSim.Path(Rate, 70);
        var echo = EchoSim.Convolve(far, h, d);
        var noise = kind switch
        {
            "stationary" => EchoSim.StationaryNoise(Rate, 16, 5, 0.01f),
            "keyboard" => EchoSim.Keyboard(Rate, 16, 5, 0.25f),
            "fan" => EchoSim.FanHum(Rate, 16, 5, 0.01f),
            _ => EchoSim.Babble(Rate, 16, 5, 0.012f)
        };
        var aec = new EchoCanceller(Rate);
        var outp = Run(aec, EchoSim.Mix(echo, noise), far);
        Assert.All(outp, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 2f));
        // everything left is noise (plus a little residual echo): far below the echo that was there
        double left = EchoSim.Rms(outp.AsSpan(Rate * 11, Rate * 5));
        double noiseRms = EchoSim.Rms(noise.AsSpan(Rate * 11, Rate * 5));
        double echoRms = EchoSim.Rms(echo.AsSpan(Rate * 11, Rate * 5));
        _out.WriteLine($"{kind}: noise {EchoSim.Db(noiseRms):0.0} dBFS, echo {EchoSim.Db(echoRms):0.0} dBFS, output {EchoSim.Db(left):0.0} dBFS");
        Assert.True(left < noiseRms + echoRms * 0.25, $"{kind}: output {left:0.0000} too high (noise {noiseRms:0.0000}, echo {echoRms:0.0000})");
    }

    [Fact]
    public void WithoutAFarEndSignal_TheMicrophoneIsPassedThroughUnchanged()
    {
        var mic = EchoSim.Speech(Rate, 6, 6);
        var silent = new float[mic.Length];
        var outp = Run(new EchoCanceller(Rate), mic, silent);
        Assert.Equal(mic, outp);
    }

    [Fact]
    public void AMutedMicrophone_StaysSilent_EvenAfterTheFilterHasLearnedAnEcho()
    {
        var far = EchoSim.Speech(Rate, 16, 7);
        var (h, d) = EchoSim.Path(Rate, 50);
        var echo = EchoSim.Convolve(far, h, d);
        var mic = (float[])echo.Clone();
        Array.Clear(mic, Rate * 10, Rate * 6); // the user mutes the microphone at 10 s while the far end keeps talking
        var outp = Run(new EchoCanceller(Rate), mic, far);
        Assert.Equal(0.0, EchoSim.Rms(outp.AsSpan(Rate * 11, Rate * 5)), 6);
    }

    [Fact]
    public void ASuddenChangeOfTheAcousticPath_DoesNotMakeThingsWorse_AndTheFilterRelearns()
    {
        // At 10 s the user moves the speaker: new delay, new gain, new reverb.
        var far = EchoSim.Speech(Rate, 26, 8);
        var (h1, d1) = EchoSim.Path(Rate, 50, gain: 0.5f, seed: 1);
        var (h2, d2) = EchoSim.Path(Rate, 130, gain: 0.8f, seed: 2);
        var e1 = EchoSim.Convolve(far, h1, d1);
        var e2 = EchoSim.Convolve(far, h2, d2);
        var echo = new float[far.Length];
        for (int i = 0; i < echo.Length; i++) echo[i] = i < Rate * 10 ? e1[i] : e2[i];
        var outp = Run(new EchoCanceller(Rate), echo, far);

        // never louder than the microphone for a sustained time (no divergence)
        int bad = 0;
        for (int i = 0; i + 1600 <= outp.Length; i += 1600)
            if (EchoSim.Rms(outp.AsSpan(i, 1600)) > EchoSim.Rms(echo.AsSpan(i, 1600)) * 2 && EchoSim.Rms(echo.AsSpan(i, 1600)) > 0.01) bad++;
        Assert.True(bad <= 2, $"{bad} blocks (100 ms) were more than 6 dB louder than the microphone");
        double erle = Erle(echo, outp, 20, 26);
        _out.WriteLine($"ERLE after the path changed: {erle:0.0} dB");
        Assert.True(erle >= 10, $"filter did not relearn: ERLE {erle:0.0} dB");
    }
}
