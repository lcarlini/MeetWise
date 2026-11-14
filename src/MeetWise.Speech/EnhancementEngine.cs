using System.Collections.Concurrent;
using System.Diagnostics;

namespace MeetWise.Speech;

/// <param name="NoiseStrength">0-100. 100 = everything the noise model removes stays removed. Lower values let a little of the original back in
/// (an attenuation limit, like NVIDIA Broadcast's strength slider), which sounds more natural in quiet rooms.</param>
/// <param name="HighPass">80 Hz high-pass before everything else (desk thumps, handling noise, hum).</param>
/// <param name="AutoGain">Speech-gated automatic gain control after the models.</param>
/// <param name="LookAheadLimiter">Clean peak limiter with 1.5 ms look-ahead (the live microphone). Off: a zero-delay soft clip, so offline tests can
/// compare output and input sample by sample.</param>
public sealed record EnhancementOptions(EnhancerKind? Kind, string? ModelPath, bool EchoCancellation,
    string? VoiceModelDir = null, string? VoiceSampleWav = null, int Threads = 1, EchoOptions? Echo = null, int MinBlockMs = 0,
    EnhancerKind? PostKind = null, string? PostModelPath = null,
    int NoiseStrength = 100, bool HighPass = false, bool AutoGain = false, bool LookAheadLimiter = false);

/// <summary>
/// Mic-side processing chain: high-pass, echo cancellation, noise suppression or voice isolation, optionally a second noise-suppression stage
/// (<see cref="EnhancementOptions.PostKind"/>: voice lock keeps your voice but does not remove steady noise, so it is followed by a denoiser),
/// the strength mix, automatic gain and a look-ahead limiter.
/// Pure and device-independent so it can be unit-tested and benchmarked offline.
/// <see cref="EnhancementOptions.MinBlockMs"/> gathers input into larger blocks before the enhancer runs: voice isolation has a fixed cost of
/// about 30 ms per call, so 10 ms blocks cost 3x real time while 80 ms blocks cost about half (measured, see docs/TEST-REPORT.md). Calls that only
/// gather return an empty array and the audio comes out with the next processed block.
/// A model that throws never silences the microphone: the block passes through unprocessed and <see cref="Errors"/> counts it.
/// </summary>
public sealed class EnhancementEngine : IDisposable
{
    private readonly SpeechEnhancer? _enhancer;
    private readonly SpeechEnhancer? _post;
    private readonly EchoCanceller? _aec;
    private readonly Biquad? _hpf;
    private readonly AutoGain? _agc;
    private readonly LookAheadLimiter? _limiter;
    private readonly StrengthMix? _mix;
    private readonly StreamResampler? _bypassResampler;
    private readonly int _inputRate;
    private long _procTicks;
    private double _audioSeconds;
    private double _recentLoad;
    private readonly int _minBlock;
    private readonly List<float> _pending = new();
    private long _windowTicks;
    private double _windowAudio;
    private long _errors;
    private float _inPeak, _outPeak;

    public int OutputSampleRate { get; }

    /// <summary>Echo settings of the live microphone.</summary>
    public static EchoOptions LiveEcho { get; set; } = new(BackgroundEstimation: true, DelayWindowMs: 1000, DelayHopMs: 125);

    /// <summary>What the echo canceller is doing (null when it is off).</summary>
    public EchoStats? EchoStats => _aec?.Stats;

    /// <summary>Smoothed per-block load (processing time / block duration) over roughly the last second.</summary>
    public double RecentLoad => _recentLoad;

    /// <summary>Processing time divided by audio time. Must stay well below 1.0 to run in real time.</summary>
    public double RealTimeFactor => _audioSeconds <= 0 ? 0 : (_procTicks / (double)Stopwatch.Frequency) / _audioSeconds;

    /// <summary>Blocks that a model failed on and that passed through unprocessed.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Peak level of the raw microphone and of the cleaned output since the last call (0..1), for level meters.</summary>
    public (float Input, float Output) TakePeaks()
    {
        var r = (_inPeak, _outPeak);
        _inPeak = 0; _outPeak = 0;
        return r;
    }

    /// <summary>Current automatic gain in dB (0 when automatic gain is off).</summary>
    public double GainDb => _agc?.GainDb ?? 0;

    /// <summary>Total delay the noise model(s) add, in milliseconds (measured once per model file).</summary>
    public double ModelLatencyMs { get; }

    public EnhancementEngine(int inputRate, EnhancementOptions options)
    {
        _inputRate = inputRate;
        _minBlock = options.MinBlockMs <= 0 ? 0 : options.MinBlockMs * inputRate / 1000;
        if (options.HighPass) _hpf = Biquad.HighPass(inputRate, 80);
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

            // the strength mix works around the noise stage: the main model when it is a denoiser, otherwise the denoiser after voice lock
            int s = Math.Clamp(options.NoiseStrength, 0, 100);
            if (s < 100)
            {
                (EnhancerKind k, string path, int from)? noise = _post is not null
                    ? (options.PostKind!.Value, options.PostModelPath!, _enhancer.OutputSampleRate)
                    : kind != EnhancerKind.VoiceIsolation ? (kind, options.ModelPath!, inputRate) : null;
                if (noise is { } n && LatencyProbe.Measure(n.k, n.path) is { } lat)
                    _mix = new StrengthMix(n.from, OutputSampleRate, lat, s);
            }
            // for the latency readout: the denoiser's measured look-ahead plus any input gathering (voice lock)
            double ms = options.MinBlockMs;
            foreach (var (k, path) in new[] { (kind, options.ModelPath), (options.PostKind, options.PostModelPath) })
                if (k is { } kk && kk != EnhancerKind.VoiceIsolation && path is not null && LatencyProbe.Measure(kk, path) is { } l)
                    ms += l * 1000.0 / OutputSampleRate;
            ModelLatencyMs = ms;
        }
        else
        {
            OutputSampleRate = inputRate;
        }
        if (_enhancer is not null && OutputSampleRate != inputRate) _bypassResampler = new StreamResampler(inputRate, OutputSampleRate);
        if (options.AutoGain) _agc = new AutoGain(OutputSampleRate);
        if (options.LookAheadLimiter) _limiter = new LookAheadLimiter(OutputSampleRate);
        // the delay search runs on a pool thread: the audio callback only copies samples into a ring
        if (options.EchoCancellation) _aec = new EchoCanceller(inputRate, options: options.Echo ?? LiveEcho);
    }

    /// <param name="mic">Mono microphone block at the input rate.</param>
    /// <param name="far">Optional far-end reference (same length) for echo cancellation.</param>
    public float[] Process(float[] mic, float[]? far = null)
    {
        if (mic.Length == 0) return mic; // a zero-length block would make the load average NaN for the rest of the session
        long t0 = Stopwatch.GetTimestamp();
        _inPeak = Math.Max(_inPeak, Peak(mic));
        var x = mic;
        if (_hpf is not null) { x = (float[])mic.Clone(); _hpf.Process(x); }
        if (_aec is not null && far is not null && far.Length == mic.Length)
            x = _aec.Process(x, far);
        bool ran = true;
        if (_enhancer is not null)
        {
            float[] input = x;
            try
            {
                if (_minBlock > 0)
                {
                    _pending.AddRange(x);
                    if (_pending.Count < _minBlock) { x = Array.Empty<float>(); ran = false; }
                    else { var chunk = _pending.ToArray(); _pending.Clear(); x = RunModels(chunk); }
                }
                else x = RunModels(x);
            }
            catch (Exception) when (_bypassResampler is not null || OutputSampleRate == _inputRate)
            {
                // never silence the call: pass the block through unprocessed (at the output rate)
                Interlocked.Increment(ref _errors);
                _pending.Clear();
                x = _bypassResampler?.Process(input) ?? input;
                ran = true;
            }
        }
        if (x.Length > 0)
        {
            _agc?.Process(x);
            if (_limiter is not null) _limiter.Process(x); else SoftLimit(x);
            _outPeak = Math.Max(_outPeak, Peak(x));
        }
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

    /// <summary>Test hook: makes the next model call throw, to prove the microphone never goes silent when a model fails.</summary>
    internal bool FailNextModelCall { get; set; }

    private float[] RunModels(float[] x)
    {
        if (FailNextModelCall) { FailNextModelCall = false; throw new InvalidOperationException("simulated model failure"); }
        var y = _enhancer!.Process(x, _inputRate);
        if (_post is null)
        {
            if (_mix is not null && y.Length > 0) y = _mix.Mix(x, y);
            return y;
        }
        if (y.Length == 0) return y;
        var z = _post.Process(y, _enhancer.OutputSampleRate);
        if (_mix is not null && z.Length > 0) z = _mix.Mix(y, z);
        return z;
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

    private static float Peak(ReadOnlySpan<float> x)
    {
        float p = 0;
        foreach (var v in x) { float a = Math.Abs(v); if (a > p) p = a; }
        return p;
    }

    public void Dispose() { _post?.Dispose(); _enhancer?.Dispose(); }
}

/// <summary>
/// Lets part of the unprocessed signal back in, time-aligned with the model output: out = wet + a * (dry - wet). With a = 0 the model output is
/// untouched; a sets how far the noise can be pushed down (an attenuation limit), so strength 50 keeps noise about 23 dB down instead of gone.
/// </summary>
internal sealed class StrengthMix
{
    private readonly StreamResampler _toOut;
    private readonly DelayLine _delay;
    private readonly float _a;
    private readonly int _cap;
    private readonly Queue<float> _dry = new();

    public StrengthMix(int dryRate, int outRate, int latencySamples, int strength)
    {
        _toOut = new StreamResampler(dryRate, outRate);
        _delay = new DelayLine(latencySamples);
        _cap = outRate; // one second
        double limitDb = 6 + strength * 0.34; // 0 → 6 dB, 50 → 23 dB, 99 → 40 dB
        _a = (float)Math.Pow(10, -limitDb / 20);
    }

    public static double AttenuationLimitDb(int strength) => strength >= 100 ? double.PositiveInfinity : 6 + Math.Clamp(strength, 0, 100) * 0.34;

    public float[] Mix(float[] dryIn, float[] wet)
    {
        var d = _toOut.Process(dryIn);
        _delay.Process(d);
        foreach (var v in d) _dry.Enqueue(v);
        var y = new float[wet.Length];
        for (int i = 0; i < wet.Length; i++)
        {
            float dry = _dry.Count > 0 ? _dry.Dequeue() : 0f;
            y[i] = wet[i] + _a * (dry - wet[i]);
        }
        // the model holds back a fixed number of samples (so the queue keeps that many), but it must never grow without bound
        while (_dry.Count > _cap) _dry.Dequeue();
        return y;
    }
}

/// <summary>
/// Measures the delay a noise model adds by running a fresh copy on a synthetic voiced signal and finding the cross-correlation peak.
/// Cached per model file. Returns null if the probe is inconclusive (the strength mix is then skipped rather than mixing misaligned audio).
/// </summary>
public static class LatencyProbe
{
    private static readonly ConcurrentDictionary<string, int?> Cache = new();

    public static int? Measure(EnhancerKind kind, string modelPath)
        => Cache.GetOrAdd(kind + "|" + modelPath, _ => Run(kind, modelPath));

    private static int? Run(EnhancerKind kind, string modelPath)
    {
        if (kind == EnhancerKind.VoiceIsolation) return null;
        try
        {
            using var e = new SpeechEnhancer(kind, modelPath);
            int rate = e.OutputSampleRate;
            var x = SyntheticVoice(rate, 1.5);
            var y = new List<float>();
            for (int i = 0; i < x.Length; i += rate / 100) y.AddRange(e.Process(x[i..Math.Min(x.Length, i + rate / 100)], rate));
            y.AddRange(e.Flush());
            int best = -1; double bestC = 0;
            int maxLag = rate / 5; // 200 ms
            var yy = y.ToArray();
            for (int lag = 0; lag <= maxLag; lag++)
            {
                double c = 0, ex = 0, ey = 0;
                for (int i = rate / 4; i + lag < yy.Length && i < x.Length; i += 2)
                {
                    c += x[i] * yy[i + lag]; ex += x[i] * x[i]; ey += yy[i + lag] * yy[i + lag];
                }
                double norm = c / Math.Sqrt(ex * ey + 1e-12);
                if (norm > bestC) { bestC = norm; best = lag; }
            }
            return bestC > 0.5 ? best : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A vowel-like signal: 130 Hz glottal pulses through three formant resonators, with a slow pitch glide so correlation has one clear peak.</summary>
    public static float[] SyntheticVoice(int rate, double seconds)
    {
        int n = (int)(rate * seconds);
        var src = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double f0 = 120 + 30 * Math.Sin(2 * Math.PI * 1.3 * i / rate);
            phase += f0 / rate;
            if (phase >= 1) { phase -= 1; src[i] = 1f; }
        }
        var y = new float[n];
        foreach (var (f, bw, g) in new[] { (700.0, 90.0, 1.0), (1220.0, 110.0, 0.6), (2600.0, 160.0, 0.3) })
        {
            double r = Math.Exp(-Math.PI * bw / rate), th = 2 * Math.PI * f / rate;
            double a1 = -2 * r * Math.Cos(th), a2 = r * r, y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                double v = src[i] - a1 * y1 - a2 * y2;
                y2 = y1; y1 = v;
                y[i] += (float)(g * v * (1 - r));
            }
        }
        float peak = y.Max(Math.Abs);
        for (int i = 0; i < n; i++) y[i] = 0.3f * y[i] / peak;
        return y;
    }
}
