using System.Numerics;

namespace MeetWise.Speech;

/// <summary>Tuning of the echo canceller beyond the basics. The defaults are what the app uses.</summary>
/// <param name="MaxDelayMs">Longest delay between the reference and the microphone that will be searched for (Bluetooth speakers reach 250 ms).</param>
/// <param name="DelayEstimation">Measure the delay with GCC-PHAT and line the reference up. Off = the filter alone has to cover the delay and adapts without waiting for evidence of an echo.</param>
/// <param name="ResidualSuppression">Attenuate what the linear filter leaves behind while only the far end is talking.</param>
/// <param name="BackgroundEstimation">Run the delay estimate on a pool thread (needed in the audio path; tests keep it inline for determinism).</param>
/// <param name="GeigelMargin">How far above the learned echo peak (as a multiple) a microphone sample must be to count as the near-end talker.</param>
public sealed record EchoOptions(int MaxDelayMs = 500, bool DelayEstimation = true, bool ResidualSuppression = true, bool BackgroundEstimation = false, float GeigelMargin = 2.5f);

/// <summary>A snapshot of what the canceller is doing, for the UI, logs and tests.</summary>
/// <param name="DelayMs">Delay applied to the reference (the measured delay minus a quarter of the filter length).</param>
/// <param name="DelayLocked">True once the delay estimator has seen the same delay repeatedly (evidence of a real echo).</param>
/// <param name="ErleDb">Echo return loss enhancement over the recent past, from frames without double talk.</param>
/// <param name="Resets">Times the filter was thrown away (diverged, or a lock turned out to be a coincidence).</param>
/// <param name="FrozenFraction">Share of the time spent with adaptation slowed down because the near-end talker was detected.</param>
public readonly record struct EchoStats(double DelayMs, bool DelayLocked, double ErleDb, int Resets, double FrozenFraction, double ResidualGain, long Samples,
    double GeigelFraction = 0, double ErrorRatioFraction = 0, double ErrorRatio = 0, double ErrorRatioBaseline = 0);

/// <summary>
/// Acoustic echo canceller: reference alignment, an NLMS adaptive filter with double-talk protection, and a conservative residual suppressor.
/// <list type="number">
/// <item>The far-end reference (what the speakers play) is delayed by the delay measured with GCC-PHAT, so the short adaptive filter only has to model
/// the room, not the device buffers. <b>Nothing adapts until that delay has been measured repeatedly</b>: with headphones, or with a reference
/// that is not what the microphone hears, there is no echo to find and the microphone passes through untouched.</item>
/// <item>The NLMS filter subtracts its estimate of the echo from the microphone. Near-end speech is detected by a Geigel test on every sample
/// (against the echo return gain learned in the first second) and by the error-to-microphone energy ratio per 10 ms frame, with a hangover.
/// Adaptation then slows to a crawl; it relaxes by itself after about a second if the error stays high, because then the room changed, not the talker.</item>
/// <item>Safety nets: a filter that makes the signal clearly louder is reset; a lock that cancels nothing for six seconds is withdrawn (two unrelated
/// signals can line up by chance); a digitally muted microphone stays silent; with no far-end signal the microphone passes through bit for bit.</item>
/// <item>The residual suppressor lowers what is left (never more than 15 dB, 20 ms attack, 5 ms release) only while it is no louder than what the filter
/// is known to leave behind (within 8 dB); anything louder is the near-end talker and passes untouched.</item>
/// </list>
/// Algorithmic latency is zero: every output sample depends only on samples up to the same instant. Not thread-safe: call from one audio thread.
/// </summary>
public sealed class EchoCanceller
{
    private const double FarFloor = 0.002;          // reference peak below this (about -54 dBFS) counts as silence
    private const float ConvergedMuFraction = 0.3f; // step size once converged: the error NLMS adds while someone else talks grows with it
    private const float DoubleTalkMu = 0.02f;       // adaptation speed (fraction of mu) while the near-end talker is detected
    private const double XiFloor = 0.06;            // error-to-microphone energy ratio that always counts as "something else is in the signal"
    private const float MinGain = 0.18f;            // -15 dB, the most the residual suppressor will ever take off

    private readonly int _rate, _taps, _maxDelay, _frame;
    private readonly float _mu, _geigel, _margin;
    private readonly float[] _w;
    private readonly float[] _hist;                 // far-end history, newest at _pos (double length for contiguous windows)
    private readonly int _histKeep;
    private readonly DelayEstimator? _estimator;
    private readonly bool _residual;
    private readonly float _farDecay, _attack, _release;

    private int _pos, _delay, _seenVersion, _sinceRecalc;
    private double _energy;                         // sum of squares of the filter window (delayed reference)
    private double _avgEnergy;                      // slow average of the window energy while the far end is active
    private float _farTrack, _micTrack;
    private float _gainPeak;                        // learned peak ratio mic/reference for echo alone: the echo return gain, which may exceed 1 (a boosted microphone)
    private bool _gainKnown, _aligned;
    private int _calFrames, _geigelPersist;
    private double _calMic, _calFar;

    // per-frame bookkeeping
    private int _inFrame;
    private double _fMic, _fErr, _fEcho;
    private double _xiSmooth, _xiBase = 1.0, _rho = 0.05, _erleDb;
    private double _eMicS, _eErrS, _eMicB = 1e-6, _eErrB = 1e-6; // short and long energy averages for the error-to-microphone ratio
    private int _geigelHold, _xiHold, _xiPersist, _badFrames, _silentRun, _resets, _lockedFrames;
    private long _samples, _frozenSamples, _geigelSamples, _xiSamples;
    private float _gain = 1f, _gainTarget = 1f;

    public int Taps => _taps;
    public int DelaySamples => _delay;

    public EchoStats Stats => new(_delay * 1000.0 / _rate, _estimator?.Locked ?? false, _erleDb, _resets,
        _samples == 0 ? 0 : _frozenSamples / (double)_samples, _gain, _samples,
        _samples == 0 ? 0 : _geigelSamples / (double)_samples, _samples == 0 ? 0 : _xiSamples / (double)_samples, _xiSmooth, _xiBase);

    /// <param name="sampleRate">Rate of both signals.</param>
    /// <param name="tailMs">Longest echo path to model after the delay (room reverb).</param>
    /// <param name="mu">NLMS step size (0.1-1).</param>
    /// <param name="geigelThreshold">Floor of the near-end threshold as a fraction of the recent reference peak.</param>
    public EchoCanceller(int sampleRate, int tailMs = 100, float mu = 0.5f, float geigelThreshold = 0.6f, EchoOptions? options = null)
    {
        options ??= new EchoOptions();
        _rate = sampleRate;
        _taps = Math.Max(Vector<float>.Count, sampleRate * tailMs / 1000 / Vector<float>.Count * Vector<float>.Count);
        _mu = mu;
        _geigel = geigelThreshold;
        _margin = options.GeigelMargin;
        _maxDelay = sampleRate * options.MaxDelayMs / 1000;
        _frame = Math.Max(1, sampleRate / 100); // 10 ms
        _residual = options.ResidualSuppression;
        _w = new float[_taps];
        _histKeep = _maxDelay + _taps + 1;
        _hist = new float[_histKeep * 2];
        _pos = _histKeep - 1;
        _farDecay = (float)Math.Exp(-1.0 / (0.125 * sampleRate));
        _attack = (float)(1 - Math.Exp(-1.0 / (0.020 * sampleRate)));
        _release = (float)(1 - Math.Exp(-1.0 / (0.005 * sampleRate)));
        if (options.DelayEstimation) _estimator = new DelayEstimator(sampleRate, options.MaxDelayMs, options.BackgroundEstimation);
    }

    /// <summary>Forget everything learned: filter, delay, history. Used when the acoustic path certainly changed.</summary>
    public void Reset()
    {
        Array.Clear(_w); Array.Clear(_hist);
        _pos = _histKeep - 1; _delay = 0; _energy = _avgEnergy = 0; _farTrack = _micTrack = 0;
        _fMic = _fErr = _fEcho = 0; _inFrame = 0;
        ResetErrorStatistics();
        _rho = 0.05;
        _geigelHold = _badFrames = _silentRun = _lockedFrames = 0;
        _gain = _gainTarget = 1f; _aligned = false;
        RestartCalibration();
        _estimator?.Reset();
        _seenVersion = _estimator?.Version ?? 0;
    }

    private void ResetErrorStatistics()
    {
        _xiSmooth = 0; _xiBase = 1.0; _erleDb = 0; _eMicS = _eErrS = 0; _eMicB = _eErrB = 1e-6;
        _xiHold = 0; _xiPersist = 0;
    }

    private void RestartCalibration()
    {
        _gainPeak = 0; _gainKnown = false; _calFrames = _geigelPersist = 0; _calMic = _calFar = 0;
    }

    /// <summary>Processes equal-length mic and far-end blocks; returns the echo-reduced microphone signal.</summary>
    public float[] Process(ReadOnlySpan<float> mic, ReadOnlySpan<float> far)
    {
        var output = new float[mic.Length];
        Process(mic, far, output);
        return output;
    }

    /// <summary>Allocation-free overload for the audio thread: writes into <paramref name="output"/> (same length as <paramref name="mic"/>; may be the same memory as mic).</summary>
    public void Process(ReadOnlySpan<float> mic, ReadOnlySpan<float> far, Span<float> output)
    {
        if (mic.Length != far.Length) throw new ArgumentException("mic and far blocks must be the same length");
        if (output.Length != mic.Length) throw new ArgumentException("output must be as long as mic");

        _estimator?.Add(mic, far);
        if (_estimator is not null && _estimator.Version != _seenVersion) ApplyDelay();

        var w = _w.AsSpan();
        int silentLimit = Math.Max(1, _rate / 200);
        for (int n = 0; n < mic.Length; n++)
        {
            Push(far[n]);
            var window = _hist.AsSpan(_pos - _delay - _taps + 1, _taps);
            float x = _hist[_pos - _delay];
            float m = mic[n];
            _farTrack = Math.Max(Math.Abs(x), _farTrack * _farDecay);
            _micTrack = Math.Max(Math.Abs(m), _micTrack * _farDecay);
            bool farActive = _farTrack > FarFloor;

            _silentRun = Math.Abs(m) < 1e-7f ? _silentRun + 1 : 0;
            bool muted = _silentRun >= silentLimit;

            float echo = farActive ? Dot(w, window) : 0f;
            float err = m - echo;
            if (muted) err = 0f; // a muted microphone must stay silent instead of receiving the negative of the echo estimate
            _samples++;

            // Adapt only while there is evidence of an echo to learn from.
            if (farActive && !muted && (_estimator is null || _estimator.Locked))
            {
                // near-end talker clearly louder than the echo has been (calibrated during the first second of far-end speech)
                if (_gainKnown && Math.Abs(m) > Math.Max(_geigel, _margin * _gainPeak) * _farTrack) _geigelHold = _rate * 150 / 1000;
                float mu = _xiBase < 0.1 ? _mu * ConvergedMuFraction : _mu;
                bool frozen = false;
                if (_geigelHold > 0) { mu *= DoubleTalkMu; frozen = true; _geigelSamples++; }
                else if (_xiHold > 0)
                {
                    // the filter does not explain the microphone: a near-end talker or a changed room. Start slow, relax with persistence.
                    mu = _mu * (DoubleTalkMu + 0.4f * Math.Min(1f, _xiPersist / 100f));
                    frozen = _xiPersist < 100;
                    _xiSamples++;
                }
                if (frozen) _frozenSamples++;
                float denom = (float)(_energy + Math.Max(_taps * 1e-6, 0.05 * _avgEnergy));
                Axpy(w, window, mu * err / denom);
            }
            if (_geigelHold > 0) _geigelHold--;

            // frame statistics
            _fMic += (double)m * m; _fErr += (double)err * err; _fEcho += (double)echo * echo;
            if (++_inFrame >= _frame) EndFrame(farActive, muted);

            // residual suppressor with smoothed gain: quick to let sound back in, slower to take it away
            _gain += (_gainTarget - _gain) * (_gainTarget < _gain ? _attack : _release);
            output[n] = err * _gain;
        }
    }

    private void ApplyDelay()
    {
        _seenVersion = _estimator!.Version;
        if (!_estimator.Locked)
        {
            // the lock was withdrawn: back to the unaligned, untrained state
            _delay = 0; _aligned = false;
            Array.Clear(_w);
            RecomputeEnergy();
            ResetErrorStatistics();
            RestartCalibration();
            return;
        }
        // Put the measured peak a quarter of the way into the filter window: early reflections stay inside it, and the estimate may wobble
        // between neighbouring reflections of a reverberant room without anything having to be relearned.
        int target = Math.Clamp(_estimator.DelaySamples - _taps / 4, 0, _maxDelay);
        // The first lock always applies (nothing has been learned yet, so nothing is lost and the level trackers get aligned);
        // later changes must be large, otherwise the filter would be thrown away for no reason.
        if (_aligned && Math.Abs(target - _delay) <= _taps / 2) return;
        _aligned = true;
        _delay = target;
        // the path moved: what the filter learned describes the old one
        Array.Clear(_w);
        RecomputeEnergy();
        ResetErrorStatistics();
        RestartCalibration();
    }

    private void EndFrame(bool farActive, bool muted)
    {
        double micE = _fMic, errE = _fErr, echoE = _fEcho;
        _fMic = _fErr = _fEcho = 0; _inFrame = 0;
        double floorE = _frame * 1e-8;

        if (farActive && !muted && _micTrack > 1e-5f)
        {
            double ratio = _micTrack / Math.Max(_farTrack, 1e-6f);
            if (!_gainKnown)
            {
                _calMic += _micTrack; _calFar += _farTrack;
                if (++_calFrames >= 100) { _gainPeak = (float)(_calMic / _calFar); _gainKnown = true; }
            }
            else if ((_geigelHold == 0 && _xiHold == 0) || _geigelPersist > 100)
                _gainPeak += 0.02f * (float)(ratio - _gainPeak); // slow and symmetric: a tracker that falls faster than it rises ratchets down and then cries wolf
            _geigelPersist = _geigelHold > 0 ? _geigelPersist + 1 : 0;
        }

        if (farActive && !muted && micE > floorE)
        {
            // Ratios of smoothed energies, not averages of per-frame ratios: a few quiet frames must not dominate the statistic.
            _eMicS += 0.2 * (micE - _eMicS); _eErrS += 0.2 * (errE - _eErrS);
            _xiSmooth = _eErrS / (_eMicS + floorE);
            if (_geigelHold == 0 && _xiHold == 0)
            {
                _eMicB += 0.02 * (micE - _eMicB); _eErrB += 0.02 * (errE - _eErrB);
                _xiBase = _eErrB / (_eMicB + floorE);
                _erleDb = -10 * Math.Log10(Math.Max(_xiBase, 1e-6));
                if (echoE > floorE) _rho += 0.05 * (Math.Min(errE / echoE, 1.0) - _rho);
            }
            // converged filter + the error suddenly large compared to its usual level: someone else is in the signal (or the room changed)
            bool xiNear = _xiBase < 0.3 && _xiSmooth > Math.Max(XiFloor, 4 * _xiBase);
            if (xiNear) { _xiHold = 15; _xiPersist = Math.Min(_xiPersist + 1, 400); }
            else
            {
                if (_xiHold > 0) _xiHold--;
                if (_xiHold == 0) _xiPersist = Math.Max(0, _xiPersist - 3);
            }

            // A Geigel alarm that the error statistic does not confirm (the filter still explains the microphone as well as usual) is a false alarm.
            if (_geigelHold > 0 && _xiBase < 0.3 && _xiSmooth < 2 * _xiBase + 0.01) _geigelHold = 0;

            // divergence guard: a filter that makes the signal clearly louder than it was is worse than none
            if (errE > 10 * micE && micE > _frame * 1e-6)
            {
                if (++_badFrames >= 5) { Array.Clear(_w); _resets++; _badFrames = 0; ResetErrorStatistics(); }
            }
            else _badFrames = 0;

            // Lock validation: an echo that is really there gets cancelled. If six seconds of far-end speech at this delay cancel nothing,
            // the "echo" the estimator saw was two unrelated signals lining up by chance: let go of it and ask for stronger evidence.
            if (_estimator is { Locked: true } && _geigelHold == 0 && _xiHold == 0)
            {
                if (++_lockedFrames >= 600 && _erleDb < 2.0)
                {
                    _estimator.Unlock();
                    ApplyDelay();
                    _lockedFrames = 0; _resets++;
                }
            }
            else if (_estimator is { Locked: false }) _lockedFrames = 0;
        }
        if (farActive) _avgEnergy += 0.02 * (_energy - _avgEnergy);

        // residual suppression target for the next frame
        _gainTarget = 1f;
        if (_residual && farActive && !muted && _geigelHold == 0 && _xiHold == 0 && _xiBase < 0.3 && echoE > floorE)
        {
            double rhatE = _rho * echoE;                  // the residual echo energy to expect after the linear filter
            double errRms = Math.Sqrt(errE / _frame);
            if (errE <= 6 * rhatE && errRms > 0.002)      // within 8 dB of the residual the filter usually leaves, and audible
                _gainTarget = (float)Math.Clamp(0.002 / errRms, MinGain, 1.0);
        }
        if (!double.IsFinite(_energy) || float.IsNaN(_w[0])) { Array.Clear(_w); _resets++; RecomputeEnergy(); }
    }

    private void Push(float x)
    {
        if (_pos == _hist.Length - 1)
        {
            // slide the newest _histKeep samples back to the start
            Array.Copy(_hist, _hist.Length - _histKeep, _hist, 0, _histKeep);
            _pos = _histKeep - 1;
        }
        _hist[++_pos] = x;
        double entering = _hist[_pos - _delay], leaving = _hist[_pos - _delay - _taps];
        _energy = Math.Max(0, _energy + entering * entering - leaving * leaving);
        if (++_sinceRecalc >= 1 << 15)
        {
            // exact recompute now and then so rounding in the running sum cannot drift over a long call
            _sinceRecalc = 0;
            RecomputeEnergy();
        }
    }

    private void RecomputeEnergy()
    {
        var win = _hist.AsSpan(_pos - _delay - _taps + 1, _taps);
        _energy = Dot(win, win);
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
