using System.Diagnostics;
using MeetWise.Speech;
using Xunit.Abstractions;

namespace MeetWise.Tests;

/// <summary>Tests that measure time cannot share the CPU with other tests: this collection runs alone, after the parallel ones.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection { }

[Collection("Timing")]
public class EchoPerformanceTests
{
    private readonly ITestOutputHelper _out;
    public EchoPerformanceTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(16000)]
    [InlineData(48000)]
    public void TenMillisecondBlocks_RunWellBelowRealTime(int rate)
    {
        if (Environment.GetEnvironmentVariable("MEETWISE_SKIP_PERF") == "1") return; // timing depends on the machine
        var far = EchoSim.Speech(rate, 20, 61);
        var rng = new Random(1);
        var mic = new float[far.Length];
        for (int i = 0; i < mic.Length; i++) mic[i] = far[Math.Max(0, i - rate / 10)] * 0.5f + (float)(rng.NextDouble() - 0.5) * 0.01f;
        var aec = new EchoCanceller(rate, options: new EchoOptions(BackgroundEstimation: true)); // as in the app: the delay search is off the audio thread
        int block = rate / 100;
        var times = new List<double>();
        var o = new float[block];
        for (int i = 0; i + block <= mic.Length; i += block)
        {
            long t0 = Stopwatch.GetTimestamp();
            aec.Process(mic.AsSpan(i, block), far.AsSpan(i, block), o);
            times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
        times.Sort();
        double mean = times.Average(), p99 = times[(int)(times.Count * 0.99)], max = times[^1];
        _out.WriteLine($"{rate} Hz, 10 ms blocks: mean {mean:0.000} ms (RTF {mean / 10:0.000}), p99 {p99:0.000} ms, worst {max:0.000} ms");
        Assert.True(mean / 10 < 0.35, $"RTF {mean / 10:0.00}");
        Assert.True(p99 < 8, $"p99 {p99:0.0} ms of a 10 ms block");
    }

    [Fact]
    public async Task TheDelaySearch_CostsTheAudioThreadAlmostNothing()
    {
        if (Environment.GetEnvironmentVariable("MEETWISE_SKIP_PERF") == "1") return;
        const int rate = 48000;
        var far = EchoSim.Speech(rate, 8, 28);
        var (h, d) = EchoSim.Path(rate, 200);
        var echo = EchoSim.Convolve(far, h, d);
        var est = new DelayEstimator(rate, background: true);
        var costs = new List<double>();
        for (int i = 0; i + 480 <= far.Length; i += 480)
        {
            long t = Stopwatch.GetTimestamp();
            est.Add(echo.AsSpan(i, 480), far.AsSpan(i, 480));
            costs.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
            while (est.IsEstimating) await Task.Delay(1); // fed faster than real time: let each estimate finish as it would between real hops
        }
        costs.Sort();
        _out.WriteLine($"audio-thread cost of the delay search per 10 ms block: median {costs[costs.Count / 2]:0.000} ms, p99 {costs[(int)(costs.Count * 0.99)]:0.000} ms, worst {costs[^1]:0.000} ms");
        Assert.True(est.Locked);
        Assert.True(costs[(int)(costs.Count * 0.99)] < 1.0, "the FFT must run on the pool thread, never in the audio callback");
    }
}
