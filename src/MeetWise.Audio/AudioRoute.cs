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

public sealed class AudioRoute : IAudioRoute
{
    public event Action<string>? Failed;

    private readonly EnhancementEngine _engine;
    private readonly MMDevice _capDevice, _renderDevice;
    private readonly WasapiCapture _capture;
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private LoopbackReference? _reference;
    private float[] _far = Array.Empty<float>();
    private long _overflows;
    private int _overloadedBlocks;

    /// <summary>Raised (once) when processing cannot keep up with real time, so the caller can switch to a lighter mode.</summary>
    public event Action? Overloaded;
    public double RecentLoad => _engine.RecentLoad;

    public long Overflows => Interlocked.Read(ref _overflows);
    public double RealTimeFactor => _engine.RealTimeFactor;

    public AudioRoute(MMDevice capture, MMDevice render, EnhancementOptions options, LoopbackReference? reference = null)
    {
        _capDevice = capture;
        _renderDevice = render;
        _reference = reference;
        _capture = new WasapiCapture(capture, true, 20);
        var cf = _capture.WaveFormat;
        _engine = new EnhancementEngine(cf.SampleRate, options);
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(_engine.OutputSampleRate, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true
        };
        ISampleProvider chain = _buffer.ToSampleProvider();
        var rf = _renderDevice.AudioClient.MixFormat;
        if (rf.SampleRate != _engine.OutputSampleRate) chain = new WdlResamplingSampleProvider(chain, rf.SampleRate);
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
        try { _reference?.Start(_capture.WaveFormat.SampleRate); }
        catch (Exception ex)
        {
            // Echo cancellation is an extra: if the loopback cannot start, the microphone must still be cleaned.
            AppLog.Warn("Echo reference unavailable, continuing without echo cancellation: " + ex.Message);
            _reference?.Dispose();
            _reference = null;
        }
        _out.Play();
        _capture.StartRecording();
        AppLog.Info($"Route started: {_capDevice.FriendlyName} -> {_renderDevice.FriendlyName}");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var fmt = _capture.WaveFormat;
            var samples = LiveTranscriber.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono = ToMono(samples, fmt.Channels);
            float[]? far = null;
            if (_reference is { } reference)
            {
                if (_far.Length != mono.Length) _far = new float[mono.Length];
                reference.Read(_far);
                far = _far;
            }
            var cleaned = _engine.Process(mono, far);
            if (_engine.RecentLoad > 0.8) { if (++_overloadedBlocks == 150) Overloaded?.Invoke(); }
            else _overloadedBlocks = 0;
            if (cleaned.Length == 0) return;
            var bytes = new byte[cleaned.Length * 4];
            Buffer.BlockCopy(cleaned, 0, bytes, 0, bytes.Length);
            if (_buffer.BufferedBytes + bytes.Length > _buffer.BufferLength) Interlocked.Increment(ref _overflows);
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            AppLog.Error("Route processing failed", ex);
        }
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

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _out.Stop(); } catch { }
        _capture.Dispose();
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

    private void StartCapture()
    {
        lock (_life)
        {
            if (_disposed) return;
            var cap = new WasapiLoopbackCapture(_speakers);
            _resampler = new ReferenceResampler(cap.WaveFormat.SampleRate, _targetRate);
            cap.DataAvailable += (_, e) => OnData(cap, e);
            cap.RecordingStopped += (_, e) => OnStopped(cap, e.Exception);
            cap.StartRecording();
            _cap = cap;
            Healthy = true;
            _attempt = 0;
        }
    }

    private void OnData(WasapiLoopbackCapture cap, WaveInEventArgs e)
    {
        try
        {
            var fmt = cap.WaveFormat;
            var floats = LiveTranscriber.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
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
