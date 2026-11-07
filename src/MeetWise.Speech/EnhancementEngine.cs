using System.Diagnostics;

namespace MeetWise.Speech;

public sealed record EnhancementOptions(EnhancerKind? Kind, string? ModelPath, bool EchoCancellation,
    string? VoiceModelDir = null, string? VoiceSampleWav = null, int Threads = 1);

/// <summary>
/// Mic-side processing chain: echo cancellation, then noise suppression or voice isolation, then a soft limiter.
/// Pure and device-independent so it can be unit-tested and benchmarked offline.
/// </summary>
public sealed class EnhancementEngine : IDisposable
{
    private readonly SpeechEnhancer? _enhancer;
    private readonly EchoCanceller? _aec;
    private readonly int _inputRate;
    private long _procTicks;
    private double _audioSeconds;
    private double _recentLoad;

    public int OutputSampleRate { get; }

    /// <summary>What the echo canceller is doing (null when it is off).</summary>
    public EchoStats? EchoStats => _aec?.Stats;

    /// <summary>Smoothed per-block load (processing time / block duration) over roughly the last second.</summary>
    public double RecentLoad => _recentLoad;

    /// <summary>Processing time divided by audio time. Must stay well below 1.0 to run in real time.</summary>
    public double RealTimeFactor => _audioSeconds <= 0 ? 0 : (_procTicks / (double)Stopwatch.Frequency) / _audioSeconds;

    public EnhancementEngine(int inputRate, EnhancementOptions options)
    {
        _inputRate = inputRate;
        if (options.Kind is { } kind)
        {
            _enhancer = kind == EnhancerKind.VoiceIsolation
                ? new SpeechEnhancer(kind, options.VoiceModelDir!, options.Threads, options.VoiceSampleWav)
                : new SpeechEnhancer(kind, options.ModelPath!, options.Threads);
            OutputSampleRate = _enhancer.OutputSampleRate;
        }
        else
        {
            OutputSampleRate = inputRate;
        }
        // the delay search runs on a pool thread: the audio callback only copies samples into a ring
        if (options.EchoCancellation) _aec = new EchoCanceller(inputRate, options: new EchoOptions(BackgroundEstimation: true));
    }

    /// <param name="mic">Mono microphone block at the input rate.</param>
    /// <param name="far">Optional far-end reference (same length) for echo cancellation.</param>
    public float[] Process(float[] mic, float[]? far = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        var x = mic;
        if (_aec is not null && far is not null && far.Length == mic.Length)
            x = _aec.Process(mic, far);
        if (_enhancer is not null)
            x = _enhancer.Process(x, _inputRate);
        SoftLimit(x);
        long elapsed = Stopwatch.GetTimestamp() - t0;
        _procTicks += elapsed;
        double blockSeconds = mic.Length / (double)_inputRate;
        _audioSeconds += blockSeconds;
        _recentLoad += 0.02 * ((elapsed / (double)Stopwatch.Frequency) / blockSeconds - _recentLoad);
        return x;
    }

    public float[] Flush() => _enhancer?.Flush() ?? Array.Empty<float>();

    private static void SoftLimit(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            if (v > 0.9f || v < -0.9f) x[i] = MathF.Tanh(v);
        }
    }

    public void Dispose() => _enhancer?.Dispose();
}
