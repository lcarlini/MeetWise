namespace MeetWise.Speech;

/// <summary>Radix-2 FFT on separate real/imaginary arrays. Length must be a power of two.</summary>
internal static class Fft
{
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}

/// <summary>
/// Finds how late the far-end reference arrives at the microphone (speaker, driver and Bluetooth buffering plus air travel) with generalised
/// cross-correlation, phase transform (GCC-PHAT) over a two-second window. The reference and the microphone come from different devices, so
/// this delay is never known in advance and is different on every PC. The estimate has to be seen twice in a row before it is used, so a
/// burst of noise cannot move it.
/// The heavy part runs on a pool thread when <c>background</c> is on, so it can never delay an audio callback.
/// </summary>
public sealed class DelayEstimator
{
    private const int DecimatedRate = 8000;

    private readonly int _rate, _factor, _window, _maxLag, _fft, _hop, _toleranceDec;
    private readonly float[] _micRing, _farRing, _snapMic, _snapFar;
    private readonly double[] _xr, _xi, _yr, _yi;
    private readonly bool _background;
    private readonly object _gate = new();
    private int _head, _filled, _sinceEstimate, _accCount, _busy;
    private double _micAcc, _farAcc;

    private int _candidate = -1, _candidateHits;
    private volatile int _delaySamples;
    private volatile bool _locked;
    private int _version;
    private double _confidence;

    /// <summary>The delay of the reference at the microphone, in samples at the input rate. 0 until <see cref="Locked"/>.</summary>
    public int DelaySamples => _delaySamples;
    public double DelayMs => _delaySamples * 1000.0 / _rate;
    public bool Locked => _locked;

    /// <summary>Peak-to-floor ratio of the last accepted estimate. Higher is more certain.</summary>
    public double Confidence => _confidence;

    /// <summary>Increments whenever the accepted delay changes.</summary>
    public int Version => Volatile.Read(ref _version);

    public int MaxDelayMs { get; }

    /// <summary>Peak-to-floor ratio an estimate needs to count. Raised after a lock that turned out not to be an echo (see <see cref="Unlock"/>).</summary>
    public double RequiredRatio { get; private set; } = 6;

    /// <summary>Peak-to-floor ratio of the most recent estimate, accepted or not (diagnostics and tests).</summary>
    public double LastRatio { get; private set; }

    /// <summary>True while a background estimate is running.</summary>
    public bool IsEstimating => Volatile.Read(ref _busy) == 1;

    public DelayEstimator(int sampleRate, int maxDelayMs = 500, bool background = false, int windowMs = 1500, int hopMs = 250)
    {
        _rate = sampleRate;
        MaxDelayMs = maxDelayMs;
        _factor = Math.Max(1, (int)Math.Round(sampleRate / (double)DecimatedRate));
        int decRate = sampleRate / _factor;
        _window = decRate * windowMs / 1000;
        _maxLag = decRate * maxDelayMs / 1000;
        _hop = decRate * hopMs / 1000;
        _toleranceDec = Math.Max(2, decRate * 6 / 1000); // 6 ms
        _fft = 1;
        while (_fft < _window + _maxLag) _fft <<= 1;
        _micRing = new float[_window];
        _farRing = new float[_window];
        _snapMic = new float[_window];
        _snapFar = new float[_window];
        _xr = new double[_fft]; _xi = new double[_fft]; _yr = new double[_fft]; _yi = new double[_fft];
        _background = background;
    }

    /// <summary>Feeds aligned microphone and reference blocks (same length, same timeline). Cheap: only decimates into a ring.</summary>
    public void Add(ReadOnlySpan<float> mic, ReadOnlySpan<float> far)
    {
        int n = Math.Min(mic.Length, far.Length);
        for (int i = 0; i < n; i++)
        {
            _micAcc += mic[i]; _farAcc += far[i];
            if (++_accCount < _factor) continue;
            _micRing[_head] = (float)(_micAcc / _factor);
            _farRing[_head] = (float)(_farAcc / _factor);
            _micAcc = _farAcc = 0; _accCount = 0;
            _head = (_head + 1) % _window;
            if (_filled < _window) _filled++;
            if (_filled == _window && ++_sinceEstimate >= _hop)
            {
                _sinceEstimate = 0;
                Launch();
            }
        }
    }

    /// <summary>
    /// Drops the current lock because the echo canceller found that cancelling at this delay achieved nothing: it was a coincidence between two
    /// unrelated signals, not an echo. The next lock needs stronger evidence.
    /// </summary>
    public void Unlock()
    {
        lock (_gate)
        {
            _candidate = -1; _candidateHits = 0;
            _delaySamples = 0; _locked = false; _confidence = 0;
            RequiredRatio = Math.Min(14, RequiredRatio + 3);
            Interlocked.Increment(ref _version);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            RequiredRatio = 6;
            _head = _filled = _sinceEstimate = _accCount = 0;
            _micAcc = _farAcc = 0;
            Array.Clear(_micRing); Array.Clear(_farRing);
            _candidate = -1; _candidateHits = 0;
            _delaySamples = 0; _locked = false; _confidence = 0;
            Interlocked.Increment(ref _version);
        }
    }

    private void Launch()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return; // the previous estimate is still running: skip this hop
        // Snapshot in time order so the worker never races the audio thread. The two buffers are reused: this point is only reached when the
        // previous estimate has finished, so nothing else is reading them, and the audio thread allocates nothing.
        var mic = _snapMic;
        var far = _snapFar;
        int first = _head; // oldest sample
        int tail = _window - first;
        Array.Copy(_micRing, first, mic, 0, tail); Array.Copy(_micRing, 0, mic, tail, first);
        Array.Copy(_farRing, first, far, 0, tail); Array.Copy(_farRing, 0, far, tail, first);
        if (_background) _ = Task.Run(() => Estimate(mic, far));
        else Estimate(mic, far);
    }

    private void Estimate(float[] mic, float[] far)
    {
        try
        {
            if (Rms(far) < 1e-3 || Rms(mic) < 1e-5) return; // no far-end speech or a silent microphone: nothing to learn from

            Prepare(far, _xr, _xi);
            Prepare(mic, _yr, _yi);
            Fft.Transform(_xr, _xi, false);
            Fft.Transform(_yr, _yi, false);

            int decRate = _rate / _factor;
            int lo = (int)(250.0 * _fft / decRate), hi = (int)(3500.0 * _fft / decRate);
            double meanMag = 0; int bins = 0;
            for (int k = lo; k <= hi; k++)
            {
                double cr = _yr[k] * _xr[k] + _yi[k] * _xi[k], ci = _yi[k] * _xr[k] - _yr[k] * _xi[k];
                meanMag += Math.Sqrt(cr * cr + ci * ci); bins++;
            }
            meanMag /= Math.Max(1, bins);
            double eps = 0.05 * meanMag + 1e-18;
            for (int k = 0; k < _fft; k++)
            {
                int kk = Math.Min(k, _fft - k);
                if (kk < lo || kk > hi) { _xr[k] = _xi[k] = 0; continue; }
                double cr = _yr[k] * _xr[k] + _yi[k] * _xi[k], ci = _yi[k] * _xr[k] - _yr[k] * _xi[k];
                double s = 1.0 / (Math.Sqrt(cr * cr + ci * ci) + eps);
                _xr[k] = cr * s; _xi[k] = ci * s;
            }
            Fft.Transform(_xr, _xi, true);

            int peakLag = 0; double peak = double.MinValue;
            for (int t = 0; t <= _maxLag; t++) if (_xr[t] > peak) { peak = _xr[t]; peakLag = t; }
            double floor = 0; int cnt = 0;
            for (int t = 0; t <= _maxLag; t++) if (Math.Abs(t - peakLag) > 4) { floor += Math.Abs(_xr[t]); cnt++; }
            floor /= Math.Max(1, cnt);
            double ratio = peak / Math.Max(floor, 1e-12);
            LastRatio = ratio;
            if (ratio < RequiredRatio) { Reject(); return; }
            Accept(peakLag, ratio);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Prepare(float[] x, double[] re, double[] im)
    {
        double mean = 0;
        for (int i = 0; i < x.Length; i++) mean += x[i];
        mean /= x.Length;
        Array.Clear(re); Array.Clear(im);
        for (int i = 0; i < x.Length; i++)
            re[i] = (x[i] - mean) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1)));
    }

    private void Reject()
    {
        lock (_gate) _candidateHits = 0; // an uncertain window breaks the "twice in a row" streak
    }

    private void Accept(int lagDecimated, double ratio)
    {
        lock (_gate)
        {
            if (_candidate >= 0 && Math.Abs(lagDecimated - _candidate) <= _toleranceDec) _candidateHits++;
            else { _candidate = lagDecimated; _candidateHits = 1; }
            if (_candidateHits < 2) return;
            int samples = _candidate * _factor;
            if (!_locked || Math.Abs(samples - _delaySamples) > _toleranceDec * _factor)
            {
                _delaySamples = samples;
                _locked = true;
                Interlocked.Increment(ref _version);
            }
            _confidence = ratio;
        }
    }

    private static double Rms(float[] x)
    {
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }
}
