using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetWise.Core.Logging;

namespace MeetWise.Audio;

/// <summary>
/// Bot-free meeting capture: microphone ("Me") and system loopback ("Others") are written to two
/// separate WAV files. Separate channels give speaker labels for free and keep each stream clean for ASR.
/// Headers are refreshed every second so a crash loses at most ~1 s.
/// </summary>
public sealed class DualChannelRecorder : IDisposable
{
    private readonly object _gate = new();      // start/stop control
    private readonly object _writeGate = new();  // writer access; never held while stopping capture
    private MMDeviceEnumerator? _enumerator;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loop;
    private WaveFileWriter? _micWriter, _loopWriter;
    private Timer? _flush;

    public string? MicPath { get; private set; }
    public string? LoopbackPath { get; private set; }
    public bool IsRecording { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public WaveFormat? MicFormat { get; private set; }
    public WaveFormat? LoopbackFormat { get; private set; }

    /// <summary>Raised with raw captured buffers (source 'mic' or 'loop') for live ASR taps.</summary>
    public event Action<string, WaveFormat, byte[], int>? DataAvailable;

    public void Start(string folder, string baseName, string micId, bool captureMic = true, bool captureLoopback = true)
    {
        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("Already recording.");
            Directory.CreateDirectory(folder);
            _enumerator = new MMDeviceEnumerator();
            try
            {
                if (captureMic)
                {
                    MicPath = Path.Combine(folder, baseName + ".mic.wav");
                    _mic = new WasapiCapture(AudioDeviceService.OpenCapture(micId, _enumerator));
                    MicFormat = _mic.WaveFormat;
                    _micWriter = new WaveFileWriter(MicPath, _mic.WaveFormat);
                    _mic.DataAvailable += (_, e) => OnData("mic", _mic.WaveFormat, _micWriter, e);
                    _mic.StartRecording();
                }
                if (captureLoopback)
                {
                    LoopbackPath = Path.Combine(folder, baseName + ".loop.wav");
                    _loop = new WasapiLoopbackCapture();
                    LoopbackFormat = _loop.WaveFormat;
                    _loopWriter = new WaveFileWriter(LoopbackPath, _loop.WaveFormat);
                    _loop.DataAvailable += (_, e) => OnData("loop", _loop.WaveFormat, _loopWriter, e);
                    _loop.StartRecording();
                }
            }
            catch
            {
                DisposeCore();
                throw;
            }
            StartedUtc = DateTime.UtcNow;
            _flush = new Timer(_ => Flush(), null, 1000, 1000);
            IsRecording = true;
            AppLog.Info($"Recording started: mic={MicPath} loop={LoopbackPath}");
        }
    }

    private void OnData(string source, WaveFormat fmt, WaveFileWriter? writer, WaveInEventArgs e)
    {
        lock (_writeGate) { writer?.Write(e.Buffer, 0, e.BytesRecorded); }
        DataAvailable?.Invoke(source, fmt, e.Buffer, e.BytesRecorded);
    }

    private void Flush()
    {
        lock (_writeGate)
        {
            try { _micWriter?.Flush(); _loopWriter?.Flush(); } catch { }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRecording) return;
            DisposeCore();
            IsRecording = false;
            AppLog.Info("Recording stopped.");
        }
    }

    private void DisposeCore()
    {
        _flush?.Dispose(); _flush = null;
        try { _mic?.StopRecording(); } catch { }
        try { _loop?.StopRecording(); } catch { }
        _mic?.Dispose(); _loop?.Dispose();
        lock (_writeGate) { _micWriter?.Dispose(); _loopWriter?.Dispose(); _micWriter = null; _loopWriter = null; }
        _enumerator?.Dispose();
        _mic = null; _loop = null; _enumerator = null;
    }

    public void Dispose() => Stop();
}
