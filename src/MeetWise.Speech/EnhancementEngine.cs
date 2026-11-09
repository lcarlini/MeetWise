using System.Diagnostics;

namespace MeetWise.Speech;

public sealed record EnhancementOptions(EnhancerKind? Kind, string? ModelPath, bool EchoCancellation,
    string? VoiceModelDir = null, string? VoiceSampleWav = null, int Threads = 1, EchoOptions? Echo = null, int MinBlockMs = 0,
    EnhancerKind? PostKind = null, string? PostModelPath = null);

/// <summary>
/// Mic-side processing chain: echo cancellation, then noise suppression or voice isolation, optionally a second noise-suppression stage
/// (<see cref="EnhancementOptions.PostKind"/>: voice lock keeps your voice but does not remove steady noise, so it is followed by a denoiser), then a soft limiter.
/// Pure and device-independent so it can be unit-tested and benchmarked offline.
/// <see cref="EnhancementOptions.MinBlockMs"/> gathers input into larger blocks before the enhancer runs: voice isolation has a fixed cost of
/// about 30 ms per call, so 10 ms blocks cost 3x real time while 80 ms blocks cost about half (measured, see docs/TEST-REPORT.md). Calls that only
/// gather return an empty array and the audio comes out with the next processed block.
/// </summary>
public sealed class EnhancementEngine : IDisposable
{
    private readonly SpeechEnhancer? _enhancer;
    private readonly SpeechEnhancer? _post;
    private readonly EchoCanceller? _aec;
    private readonly int _inputRate;
    private long _procTicks;
    private double _audioSeconds;
    private double _recentLoad;
    private readonly int _minBlock;
    private readonly List<float> _pending = new();
    private long _windowTicks;
    private double _windowAudio;

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
        _minBlock = options.MinBlockMs <= 0 ? 0 : options.MinBlockMs * inputRate / 1000;
        if (options.Kind is { } kind)
        {
            _enhancer = kind == EnhancerKind.VoiceIsolation
                ? new SpeechEnhancer(kind, options.VoiceModelDir!, options.Threads, options.VoiceSampleWav)
                : new SpeechEnhancer(kind, options.ModelPath!, options.Threads);
            OutputSampleRate = _enhancer.OutputSampleRate;
            if (options.PostKind is { } post && options.PostModelPath is { } postModel && post != EnhancerKind.VoiceIsolation)
            {
                _post = new SpeechEnhancer(post, postModel, options.Threads);
                OutputSampleRate = _post.OutputSampleRate;
            }
        }
        else
        {
            OutputSampleRate = inputRate;
        }
        // the delay search runs on a pool thread: the audio callback only copies samples into a ring
        if (options.EchoCancellation) _aec = new EchoCanceller(inputRate, options: options.Echo ?? new EchoOptions(BackgroundEstimation: true));
    }

    /// <param name="mic">Mono microphone block at the input rate.</param>
    /// <param name="far">Optional far-end reference (same length) for echo cancellation.</param>
    public float[] Process(float[] mic, float[]? far = null)
    {
        if (mic.Length == 0) return mic; // a zero-length block would make the load average NaN for the rest of the session
        long t0 = Stopwatch.GetTimestamp();
        var x = mic;
        if (_aec is not null && far is not null && far.Length == mic.Length)
            x = _aec.Process(mic, far);
        bool ran = true;
        if (_enhancer is not null)
        {
            if (_minBlock > 0)
            {
                _pending.AddRange(x);
                if (_pending.Count < _minBlock) { x = Array.Empty<float>(); ran = false; }
                else { var chunk = _pending.ToArray(); _pending.Clear(); x = _enhancer.Process(chunk, _inputRate); }
            }
            else x = _enhancer.Process(x, _inputRate);
            if (_post is not null && x.Length > 0) x = _post.Process(x, _enhancer.OutputSampleRate);
        }
        SoftLimit(x);
        long elapsed = Stopwatch.GetTimestamp() - t0;
        _procTicks += elapsed;
        double blockSeconds = mic.Length / (double)_inputRate;
        _audioSeconds += blockSeconds;
        _windowTicks += elapsed;
        _windowAudio += blockSeconds;
        if (ran)
        {
            // one load sample per processed block, weighted by how much audio it covered, so gathering does not look like idle time
            double load = (_windowTicks / (double)Stopwatch.Frequency) / _windowAudio;
            _recentLoad += Math.Min(1.0, 0.02 * (_windowAudio / blockSeconds)) * (load - _recentLoad);
            _windowTicks = 0; _windowAudio = 0;
        }
        return x;
    }

    public float[] Flush()
    {
        if (_enhancer is null) return Array.Empty<float>();
        var rest = _pending.Count == 0 ? Array.Empty<float>() : _enhancer.Process(_pending.ToArray(), _inputRate);
        _pending.Clear();
        var first = rest.Concat(_enhancer.Flush()).ToArray();
        if (_post is null) return first;
        var y = first.Length > 0 ? _post.Process(first, _enhancer.OutputSampleRate) : Array.Empty<float>();
        return y.Concat(_post.Flush()).ToArray();
    }

    private static void SoftLimit(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            if (v > 0.9f || v < -0.9f) x[i] = MathF.Tanh(v);
        }
    }

    public void Dispose() { _post?.Dispose(); _enhancer?.Dispose(); }
}
