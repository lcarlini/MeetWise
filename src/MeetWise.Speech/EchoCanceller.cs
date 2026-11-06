using System.Numerics;

namespace MeetWise.Speech;

/// <summary>
/// Adaptive echo canceller (NLMS with a Geigel double-talk detector). The far-end reference is the
/// audio played on the speakers; it is subtracted, after filtering through the learned room response,
/// from the microphone. Adaptation freezes while the near-end talker is active so their voice is kept.
/// </summary>
public sealed class EchoCanceller
{
    private readonly int _taps;
    private readonly float _mu;
    private readonly float[] _w;
    private readonly float[] _hist;   // far-end history, newest at the end (double length for contiguous windows)
    private int _pos;                 // index of newest far sample inside _hist
    private float _farPeak;
    private int _sinceRecalc;
    private float _energy;           // running sum of squares of the far-end window
    private readonly float _geigel;

    public int Taps => _taps;

    /// <param name="sampleRate">Rate of both signals.</param>
    /// <param name="tailMs">Longest echo path to model (room reverb + device latency).</param>
    /// <param name="mu">NLMS step size (0.1-1).</param>
    /// <param name="geigelThreshold">Near-end is considered active when |mic| exceeds this fraction of the recent far peak.</param>
    public EchoCanceller(int sampleRate, int tailMs = 100, float mu = 0.5f, float geigelThreshold = 0.9f)
    {
        _taps = Math.Max(Vector<float>.Count, sampleRate * tailMs / 1000 / Vector<float>.Count * Vector<float>.Count);
        _mu = mu;
        _geigel = geigelThreshold;
        _w = new float[_taps];
        _hist = new float[_taps * 2];
        _pos = _taps - 1;
    }

    /// <summary>Processes equal-length mic and far-end blocks; returns the echo-reduced microphone signal.</summary>
    public float[] Process(ReadOnlySpan<float> mic, ReadOnlySpan<float> far)
    {
        if (mic.Length != far.Length) throw new ArgumentException("mic and far blocks must be the same length");
        var output = new float[mic.Length];
        var w = _w.AsSpan();
        for (int n = 0; n < mic.Length; n++)
        {
            Push(far[n]);
            var window = _hist.AsSpan(_pos - _taps + 1, _taps);

            float echo = Dot(w, window);
            float err = mic[n] - echo;
            output[n] = err;

            _farPeak = Math.Max(Math.Abs(far[n]), _farPeak * 0.9995f);
            bool nearActive = Math.Abs(mic[n]) > _geigel * _farPeak && _farPeak > 1e-4f;
            if (!nearActive && _farPeak > 1e-4f)
            {
                float energy = _energy + 1e-3f;
                Axpy(w, window, _mu * err / energy);
            }
        }
        return output;
    }

    private void Push(float x)
    {
        if (_pos == _hist.Length - 1)
        {
            // slide the newest (taps-1) samples back to the start
            Array.Copy(_hist, _hist.Length - (_taps - 1), _hist, 0, _taps - 1);
            _pos = _taps - 2;
        }
        _hist[++_pos] = x;
        float leaving = _pos - _taps >= 0 ? _hist[_pos - _taps] : 0f;
        _energy = Math.Max(0f, _energy + x * x - leaving * leaving);
        if (++_sinceRecalc >= 1 << 15)
        {
            // Exact recompute now and then so float rounding in the running sum cannot drift over a long call.
            _sinceRecalc = 0;
            _energy = Dot(_hist.AsSpan(_pos - _taps + 1, _taps), _hist.AsSpan(_pos - _taps + 1, _taps));
        }
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var acc = Vector<float>.Zero;
        int i = 0, step = Vector<float>.Count;
        for (; i <= a.Length - step; i += step)
            acc += new Vector<float>(a.Slice(i, step)) * new Vector<float>(b.Slice(i, step));
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static void Axpy(Span<float> w, ReadOnlySpan<float> x, float g)
    {
        int i = 0, step = Vector<float>.Count;
        var gv = new Vector<float>(g);
        for (; i <= w.Length - step; i += step)
            (new Vector<float>(w.Slice(i, step)) + gv * new Vector<float>(x.Slice(i, step))).CopyTo(w.Slice(i, step));
        for (; i < w.Length; i++) w[i] += g * x[i];
    }
}
