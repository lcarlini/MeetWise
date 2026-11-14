using MeetWise.Core.Logging;
using MeetWise.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetWise.Audio;

/// <summary>
/// Real-time route: capture device, then EnhancementEngine, then render device. Point the render side at a
/// virtual cable input (or the MeetWise virtual microphone) so meeting apps receive the cleaned voice, or route
/// incoming meeting audio through it to clean the other participants.
/// </summary>
public interface IAudioRoute : IDisposable
{
    void Start();

    /// <summary>Raised when capture or playback stops because of an error (device unplugged, driver restarted, format change).</summary>
    event Action<string>? Failed;

    double RecentLoad { get; }
    long Overflows { get; }
}

/// <summary>Live numbers of a running route for the level meters and the status line.</summary>
public sealed record RouteMeter(float InputPeak, float OutputPeak, double GainDb, double LatencyMs, long ModelErrors, double Load);

public sealed class AudioRoute : IAudioRoute
{
    public event Action<string>? Failed;

    private readonly EnhancementEngine _engine;
    private readonly MMDevice _capDevice, _renderDevice;
    private readonly WasapiCapture _capture;
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly StreamResampler? _toRender;
    private LoopbackReference? _reference;
    private float[] _far = Array.Empty<float>();
    private long _overflows;
    private int _overloadedBlocks;
    private int _consecutiveErrors;
    private float[]? _probe;
    private int _probePos;
    private readonly object _probeLock = new();

    /// <summary>The output buffer is kept between these bounds: the capture and the cable run on different clocks, and without a bound the
    /// difference piles up as delay (up to the whole buffer, half a second, after a long call).</summary>
    private static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(120), TrimTo = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the microphone may take to start before it is treated as hung.</summary>
    public static TimeSpan MicStartTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised with the device id when the microphone does not start in time (the caller should pick another one).</summary>
    public event Action<string>? DeviceHung;

    /// <summary>Raised (once) when processing cannot keep up with real time, so the caller can switch to a lighter mode.</summary>
    public event Action? Overloaded;
    public double RecentLoad => _engine.RecentLoad;

    public long Overflows => Interlocked.Read(ref _overflows);
    public double RealTimeFactor => _engine.RealTimeFactor;

    private readonly AccentStage? _accent;

    /// <param name="accentFor">Optional: gives the Accent Assist stage for the chain's output rate (null = off).</param>
    public AudioRoute(MMDevice capture, MMDevice render, EnhancementOptions options, LoopbackReference? reference = null, Func<int, AccentStage?>? accentFor = null)
    {
        _capDevice = capture;
        _renderDevice = render;
        _reference = reference;
        _capture = new WasapiCapture(capture, true, 20);
        var cf = _capture.WaveFormat;
        _engine = new EnhancementEngine(cf.SampleRate, options);
        _accent = accentFor?.Invoke(_engine.OutputSampleRate);
        var rf = _renderDevice.AudioClient.MixFormat;
        // resample here with the windowed-sinc converter (NAudio's WdlResamplingSampleProvider uses a linear + IIR mode that smears the phase)
        if (rf.SampleRate != _engine.OutputSampleRate) _toRender = new StreamResampler(_engine.OutputSampleRate, rf.SampleRate);
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rf.SampleRate, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true
        };
        ISampleProvider chain = _buffer.ToSampleProvider();
        if (rf.Channels == 2) chain = new MonoToStereoSampleProvider(chain);
        _out = new WasapiOut(render, AudioClientShareMode.Shared, true, 30);
        _out.Init(chain);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Failed?.Invoke("Capture stopped: " + e.Exception.Message); };
        _out.PlaybackStopped += (_, e) => { if (e.Exception is not null) Failed?.Invoke("Playback stopped: " + e.Exception.Message); };
    }

    /// <summary>What the echo canceller is doing right now (null when echo cancellation is off).</summary>
    public EchoStats? EchoStats => _engine.EchoStats;

    public void Start()
    {
        AppLog.Info($"Route starting: echo reference {(_reference is null ? "off" : "on")}, output {_renderDevice.FriendlyName}, input {_capDevice.FriendlyName}");
        try { _reference?.Start(_capture.WaveFormat.SampleRate); }
        catch (Exception ex)
        {
            // Echo cancellation is an extra: if the loopback cannot start, the microphone must still be cleaned.
            AppLog.Warn("Echo reference unavailable, continuing without echo cancellation: " + ex.Message);
            _reference?.Dispose();
            _reference = null;
        }
        AppLog.Info("Route: echo reference ready; starting playback into the feed");
        _out.Play();
        AppLog.Info("Route: playback started; starting the microphone");
        // some virtual microphones never return from Start (measured: NVIDIA Broadcast on the user's PC); never hang the route on one
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() => { try { _capture.StartRecording(); } catch (Exception ex) { error = ex; } finally { done.Set(); } })
            { IsBackground = true, Name = "MeetWise mic start" };
        t.Start();
        if (!done.Wait(MicStartTimeout))
        {
            AppLog.Warn($"The microphone '{_capDevice.FriendlyName}' did not start within {MicStartTimeout.TotalSeconds:0.#} s.");
            _micHung = true;
            DeviceHung?.Invoke(_capDevice.ID);
            throw new TimeoutException($"The microphone '{_capDevice.FriendlyName}' does not respond.");
        }
        if (error is not null) throw error;
        AppLog.Info($"Route started: {_capDevice.FriendlyName} -> {_renderDevice.FriendlyName}");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var fmt = _capture.WaveFormat;
            var samples = Pcm.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono = ToMono(samples, fmt.Channels);
            float[]? far = null;
            if (_reference is { } reference)
            {
                if (_far.Length != mono.Length) _far = new float[mono.Length];
                reference.Read(_far);
                far = _far;
            }
            var cleaned = _engine.Process(mono, far);
            if (_accent is not null && cleaned.Length > 0) cleaned = _accent.Process(cleaned);
            MixProbe(cleaned);
            if (_engine.RecentLoad > 0.8) { if (++_overloadedBlocks == 150) Overloaded?.Invoke(); }
            else _overloadedBlocks = 0;
            if (cleaned.Length == 0) return;
            if (_toRender is not null) cleaned = _toRender.Process(cleaned);
            var bytes = new byte[cleaned.Length * 4];
            Buffer.BlockCopy(cleaned, 0, bytes, 0, bytes.Length);
            if (_buffer.BufferedBytes + bytes.Length > _buffer.BufferLength) Interlocked.Increment(ref _overflows);
            _buffer.AddSamples(bytes, 0, bytes.Length);
            TrimQueue();
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Route processing failed", ex);
            // a route that keeps failing sends silence while looking "on": after half a second of failures, rebuild it
            if (++_consecutiveErrors == 25) Failed?.Invoke("processing kept failing: " + ex.Message);
        }
    }

    private void TrimQueue()
    {
        if (_buffer.BufferedDuration <= MaxQueued) return;
        int excess = _buffer.BufferedBytes - (int)(_buffer.WaveFormat.AverageBytesPerSecond * TrimTo.TotalSeconds);
        excess -= excess % _buffer.WaveFormat.BlockAlign;
        if (excess <= 0) return;
        var scratch = new byte[excess];
        _buffer.Read(scratch, 0, excess);
        Interlocked.Increment(ref _trims);
    }

    private long _trims;

    /// <summary>Times queued audio was dropped to keep the delay low (clock drift between the microphone and the cable).</summary>
    public long Trims => Interlocked.Read(ref _trims);

    /// <summary>
    /// Mixes a test signal into the cleaned output (after all processing), so a self-test can check that what MeetWise writes really arrives on
    /// the "MeetWise Microphone" that Zoom/Teams record from.
    /// </summary>
    public void InjectProbe(float[] signalAtOutputRate)
    {
        lock (_probeLock) { _probe = signalAtOutputRate; _probePos = 0; }
    }

    private void MixProbe(float[] block)
    {
        if (_probe is null) return;
        lock (_probeLock)
        {
            if (_probe is null) return;
            int n = Math.Min(block.Length, _probe.Length - _probePos);
            for (int i = 0; i < n; i++) block[i] = Math.Clamp(block[i] + _probe[_probePos + i], -1f, 1f);
            _probePos += n;
            if (_probePos >= _probe.Length) _probe = null;
        }
    }

    public int OutputSampleRate => _engine.OutputSampleRate;

    /// <summary>Why echo cancellation is not active right now (null when it is, or when it is switched off).</summary>
    public string? EchoProblem => _reference?.Problem;

    public RouteMeter TakeMeter()
    {
        var (i, o) = _engine.TakePeaks();
        double queued = _buffer.BufferedDuration.TotalMilliseconds;
        // what the caller hears late: capture buffer + model look-ahead + queue + playback buffer
        double latency = 20 + _engine.ModelLatencyMs + (_accent?.DelayMs ?? 0) + queued + 30;
        return new RouteMeter(i, o, _engine.GainDb, latency, _engine.Errors, _engine.RecentLoad);
    }

    public static float[] ToMono(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += interleaved[i * channels + c];
            mono[i] = s / channels;
        }
        return mono;
    }

    private volatile bool _micHung;

    public void Dispose()
    {
        // a capture stuck inside Start would block Stop/Dispose as well: abandon it (its thread is a background thread)
        if (!_micHung) try { _capture.StopRecording(); } catch { }
        try { _out.Stop(); } catch { }
        if (!_micHung) _capture.Dispose();
        _out.Dispose();
        _reference?.Dispose();
        _engine.Dispose();
    }
}

/// <summary>
/// Captures what plays on a speaker endpoint so the echo canceller knows what to remove. The capture is converted to mono at the
/// microphone's sample rate and placed on a <see cref="ReferenceTimeline"/>. If the loopback stream dies (speakers changed their format, driver
/// reset, device removed) it restarts itself with growing delays and the reference is silent in between: the microphone path is never affected.
/// </summary>
public sealed class LoopbackReference : IDisposable
{
    private readonly MMDevice _speakers;
    private readonly object _life = new();
    private WasapiLoopbackCapture? _cap;
    private ReferenceTimeline? _timeline;
    private ReferenceResampler? _resampler;
    private Timer? _retry;
    private int _targetRate, _attempt;
    private bool _disposed;

    /// <summary>True while a loopback stream is running.</summary>
    public bool Healthy { get; private set; }

    /// <summary>Times the loopback stream had to be restarted.</summary>
    public int Restarts { get; private set; }

    public LoopbackReference(MMDevice speakers) => _speakers = speakers;

    /// <param name="targetRate">Sample rate of the microphone blocks the reference will be read against.</param>
    public void Start(int targetRate)
    {
        _targetRate = targetRate;
        _timeline = new ReferenceTimeline(targetRate);
        StartCapture();
    }

    /// <summary>How long opening the loopback may take before the microphone goes on without it.</summary>
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Opens and starts the loopback stream on its own thread. Some virtual speakers never return from Initialize/Start (measured: the NVIDIA
    /// Broadcast speakers on the user's PC): that froze the whole microphone route, so Zoom/Teams heard silence. After
    /// <see cref="StartTimeout"/> the microphone goes on without echo cancellation; if the stream starts later it is used from then on.
    /// </summary>
    private void StartCapture()
    {
        lock (_life) { if (_disposed) return; }
        var done = new ManualResetEventSlim();
        Exception? error = null;
        WasapiLoopbackCapture? started = null;
        var t = new Thread(() =>
        {
            WasapiLoopbackCapture? cap = null;
            try
            {
                cap = new WasapiLoopbackCapture(_speakers);
                var resampler = new ReferenceResampler(cap.WaveFormat.SampleRate, _targetRate);
                var c = cap;
                c.DataAvailable += (_, e) => OnData(c, e);
                c.RecordingStopped += (_, e) => OnStopped(c, e.Exception);
                _resampler = resampler;
                cap.StartRecording();
                bool keep;
                lock (_life)
                {
                    keep = !_disposed;
                    if (keep) { _cap = cap; Healthy = true; _attempt = 0; }
                }
                if (!keep) { try { cap.StopRecording(); cap.Dispose(); } catch (Exception) { } }
                else started = cap;
                if (done.IsSet) AppLog.Info("Echo reference started late; echo cancellation is active now.");
            }
            catch (Exception ex)
            {
                error = ex;
                try { cap?.Dispose(); } catch (Exception) { }
            }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "MeetWise loopback start" };
        t.Start();
        if (!done.Wait(StartTimeout))
        {
            Problem = "The speakers did not answer, so echo cancellation is off for now.";
            AppLog.Warn($"Echo reference: the loopback of '{_speakers.FriendlyName}' did not start within {StartTimeout.TotalSeconds:0.#} s; the microphone continues without echo cancellation.");
            done.Set(); // a late start logs that it became active
            return;
        }
        if (error is not null) throw error;
        Problem = null;
    }

    /// <summary>Why echo cancellation is not active (null when it is).</summary>
    public string? Problem { get; private set; }

    private void OnData(WasapiLoopbackCapture cap, WaveInEventArgs e)
    {
        try
        {
            var fmt = cap.WaveFormat;
            var floats = Pcm.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono = AudioRoute.ToMono(floats, fmt.Channels);
            var timeline = _timeline!;
            _resampler!.Process(mono, timeline.Write);
        }
        catch (Exception ex) { AppLog.Warn("Echo reference block dropped: " + ex.Message); }
    }

    private void OnStopped(WasapiLoopbackCapture cap, Exception? error)
    {
        lock (_life)
        {
            if (_disposed || !ReferenceEquals(cap, _cap)) return;
            Healthy = false;
            _cap = null;
            ScheduleRestart(error?.Message);
        }
        // outside the lock: disposing joins the capture thread, and Dispose() holds the lock while it waits for that same thread
        try { cap.Dispose(); } catch (Exception) { }
    }

    private void ScheduleRestart(string? why)
    {
        // caller holds _life
        _attempt++;
        var delay = TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, _attempt - 1)));
        AppLog.Warn($"Echo reference stopped ({why ?? "no error"}); restarting in {delay.TotalSeconds:0.#} s.");
        _retry?.Dispose();
        _retry = new Timer(_ =>
        {
            try
            {
                Restarts++;
                StartCapture();
            }
            catch (Exception ex)
            {
                lock (_life) { if (!_disposed) ScheduleRestart(ex.Message); }
            }
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Fills <paramref name="dest"/> with the reference for the next microphone block (zeros while the reference is unavailable).</summary>
    public void Read(Span<float> dest)
    {
        var t = _timeline;
        if (t is null) { dest.Clear(); return; }
        t.Read(dest);
    }

    public void Dispose()
    {
        WasapiLoopbackCapture? cap;
        lock (_life)
        {
            _disposed = true;
            _retry?.Dispose();
            cap = _cap;
            _cap = null;
        }
        // never join the capture thread while holding the lock its RecordingStopped handler needs (that deadlocked the whole window)
        if (cap is not null)
        {
            try { cap.StopRecording(); } catch (Exception) { }
            try { cap.Dispose(); } catch (Exception) { }
        }
    }
}
