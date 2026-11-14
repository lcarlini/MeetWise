namespace MeetWise.Speech;

/// <summary>Second-order IIR section (RBJ cookbook). Used as an 80 Hz high-pass that removes desk thumps, handling noise and mains hum harmonics below speech.</summary>
public sealed class Biquad
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0);
    }

    public static Biquad HighPass(int sampleRate, double cutoffHz, double q = 0.7071)
    {
        double w = 2 * Math.PI * cutoffHz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void Process(Span<float> x)
    {
        float x1 = _x1, x2 = _x2, y1 = _y1, y2 = _y2;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            float y = _b0 * v + _b1 * x1 + _b2 * x2 - _a1 * y1 - _a2 * y2;
            x2 = x1; x1 = v; y2 = y1; y1 = y;
            x[i] = y;
        }
        // flush denormals so a long silence does not make the filter slow
        _x1 = Math.Abs(x1) < 1e-20f ? 0 : x1; _x2 = Math.Abs(x2) < 1e-20f ? 0 : x2;
        _y1 = Math.Abs(y1) < 1e-20f ? 0 : y1; _y2 = Math.Abs(y2) < 1e-20f ? 0 : y2;
    }
}

/// <summary>
/// Slow, speech-gated automatic gain control. It measures the level only while someone is speaking (10 ms frames clearly above the tracked noise
/// floor), moves the gain at most a few dB per second, and freezes it in pauses, so it never "breathes" background noise up between sentences.
/// </summary>
public sealed class AutoGain
{
    private readonly int _frame;
    private readonly double _targetDb, _maxBoostDb, _maxCutDb, _upDbPerSec, _downDbPerSec;
    private double _floorDb = -70, _gainDb, _speechDb = double.NaN;
    private float _currentGain = 1f;
    private readonly float[] _hold;
    private int _held;

    public double GainDb => _gainDb;
    public bool SpeechActive { get; private set; }

    public AutoGain(int sampleRate, double targetDb = -20, double maxBoostDb = 18, double maxCutDb = 12, double upDbPerSec = 4, double downDbPerSec = 18)
    {
        _frame = sampleRate / 100;
        _hold = new float[_frame];
        _targetDb = targetDb; _maxBoostDb = maxBoostDb; _maxCutDb = maxCutDb; _upDbPerSec = upDbPerSec; _downDbPerSec = downDbPerSec;
    }

    /// <summary>Applies the gain in place. Gain changes are ramped across each 10 ms frame (no zipper noise).</summary>
    public void Process(Span<float> x)
    {
        int i = 0;
        while (i < x.Length)
        {
            int n = Math.Min(_frame - _held, x.Length - i);
            // measure on the incoming (ungained) signal
            x.Slice(i, n).CopyTo(_hold.AsSpan(_held));
            _held += n;
            float target = (float)Math.Pow(10, _gainDb / 20);
            float start = _currentGain, step = (target - start) / Math.Max(1, n);
            for (int k = 0; k < n; k++) { start += step; x[i + k] *= start; }
            _currentGain = target;
            i += n;
            if (_held == _frame) { Update(_hold); _held = 0; }
        }
    }

    private void Update(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var v in frame) sum += v * v;
        double db = 10 * Math.Log10(sum / frame.Length + 1e-12);
        // noise floor: falls fast, rises slowly (about 3 dB per second)
        _floorDb = db < _floorDb ? _floorDb + 0.2 * (db - _floorDb) : _floorDb + 0.03;
        SpeechActive = db > Math.Max(_floorDb + 12, -55);
        if (!SpeechActive) return;
        _speechDb = double.IsNaN(_speechDb) ? db : _speechDb + 0.05 * (db - _speechDb);
        double wanted = Math.Clamp(_targetDb - _speechDb, -_maxCutDb, _maxBoostDb);
        double maxStep = (wanted > _gainDb ? _upDbPerSec : _downDbPerSec) / 100.0;
        _gainDb += Math.Clamp(wanted - _gainDb, -maxStep, maxStep);
    }
}

/// <summary>
/// Peak limiter with look-ahead: the gain starts to fall before a loud peak arrives, so plosives and laughs never clip and never distort the way
/// a hard clip or a tanh does. Adds <see cref="LatencySamples"/> of delay (1.5 ms).
/// </summary>
public sealed class LookAheadLimiter
{
    private readonly float _ceiling;
    private readonly float[] _delay;
    private readonly float _release;
    private int _pos;
    private float _gain = 1f;
    private readonly float[] _peakWindow;

    public int LatencySamples => _delay.Length;

    public LookAheadLimiter(int sampleRate, double ceilingDb = -1, double lookAheadMs = 1.5, double releaseMs = 60)
    {
        _ceiling = (float)Math.Pow(10, ceilingDb / 20);
        _delay = new float[Math.Max(1, (int)(sampleRate * lookAheadMs / 1000))];
        _peakWindow = new float[_delay.Length];
        _release = (float)(1 - Math.Exp(-1.0 / (sampleRate * releaseMs / 1000)));
    }

    public void Process(Span<float> x)
    {
        int n = _delay.Length;
        for (int i = 0; i < x.Length; i++)
        {
            float input = x[i];
            float outgoing = _delay[_pos];
            _delay[_pos] = input;
            _peakWindow[_pos] = Math.Abs(input);
            _pos = (_pos + 1) % n;
            // the largest peak still to leave the delay line, including the sample leaving right now
            float peak = Math.Abs(outgoing);
            for (int k = 0; k < n; k++) if (_peakWindow[k] > peak) peak = _peakWindow[k];
            float needed = peak > _ceiling ? _ceiling / peak : 1f;
            _gain = needed < _gain ? needed : _gain + _release * (1f - _gain);
            x[i] = outgoing * _gain;
        }
    }
}

/// <summary>Fixed delay line (keeps the unprocessed signal time-aligned with a model's output for the strength mix).</summary>
public sealed class DelayLine
{
    private readonly float[] _buf;
    private int _pos;
    public DelayLine(int samples) => _buf = new float[Math.Max(0, samples)];

    public void Process(Span<float> x)
    {
        if (_buf.Length == 0) return;
        for (int i = 0; i < x.Length; i++)
        {
            float v = _buf[_pos];
            _buf[_pos] = x[i];
            x[i] = v;
            _pos = (_pos + 1) % _buf.Length;
        }
    }
}

/// <summary>Streaming mono sample-rate converter (NAudio's WDL resampler in windowed-sinc mode).</summary>
public sealed class StreamResampler
{
    private readonly NAudio.Dsp.WdlResampler? _r;
    private readonly double _ratio;
    public StreamResampler(int fromRate, int toRate)
    {
        _ratio = toRate / (double)fromRate;
        if (fromRate == toRate) return;
        _r = new NAudio.Dsp.WdlResampler();
        // windowed-sinc interpolation (64 taps): the default linear + IIR mode shifts phase and costs about 15 dB of SI-SDR on a round trip
        _r.SetMode(true, 0, true, 64);
        _r.SetFilterParms();
        _r.SetFeedMode(true);
        _r.SetRates(fromRate, toRate);
    }

    public float[] Process(float[] input)
    {
        if (_r is null || input.Length == 0) return input;
        int needed = _r.ResamplePrepare(input.Length, 1, out var inBuf, out int inOff);
        Array.Copy(input, 0, inBuf, inOff, needed);
        int max = (int)(input.Length * _ratio) + 16;
        var output = new float[max];
        int produced = _r.ResampleOut(output, 0, needed, max, 1);
        if (produced == max) return output;
        Array.Resize(ref output, produced);
        return output;
    }
}
