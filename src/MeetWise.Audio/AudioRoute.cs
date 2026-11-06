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
public sealed class AudioRoute : IDisposable
{
    private readonly EnhancementEngine _engine;
    private readonly MMDevice _capDevice, _renderDevice;
    private readonly WasapiCapture _capture;
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly LoopbackReference? _reference;
    private long _overflows;

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
    }

    public void Start()
    {
        _reference?.Start();
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
            var far = _reference?.Read(mono.Length);
            var cleaned = _engine.Process(mono, far);
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

/// <summary>Captures what plays on a speaker endpoint so the echo canceller knows what to remove.</summary>
public sealed class LoopbackReference : IDisposable
{
    private readonly WasapiLoopbackCapture _cap;
    private readonly Queue<float> _ring = new();
    private readonly object _gate = new();
    private const int MaxSamples = 48000;

    public LoopbackReference(MMDevice speakers) => _cap = new WasapiLoopbackCapture(speakers);

    public void Start()
    {
        _cap.DataAvailable += (_, e) =>
        {
            var fmt = _cap.WaveFormat;
            var floats = LiveTranscriber.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono = AudioRoute.ToMono(floats, fmt.Channels);
            lock (_gate)
            {
                foreach (var v in mono) _ring.Enqueue(v);
                while (_ring.Count > MaxSamples) _ring.Dequeue();
            }
        };
        _cap.StartRecording();
    }

    /// <summary>Returns exactly count samples, zero-padded when the reference is behind.</summary>
    public float[] Read(int count)
    {
        var o = new float[count];
        lock (_gate)
        {
            for (int i = 0; i < count && _ring.Count > 0; i++) o[i] = _ring.Dequeue();
        }
        return o;
    }

    public void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}
