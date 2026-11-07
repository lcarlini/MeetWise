using MeetWise.Speech;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetWise.Audio;

/// <summary>Records from a physical microphone until stopped and returns mono 16 kHz audio. Used by voice training and the voice-lock test.</summary>
public sealed class ClipRecorder : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly WasapiCapture _capture;
    private readonly Resampler _resampler;
    private readonly List<float> _samples = new();
    private readonly object _gate = new();
    private bool _recording;

    /// <summary>Peak level (0 to 1) of each captured buffer, for a level meter.</summary>
    public event Action<float>? Level;

    /// <param name="micId">A device id or "default". The virtual MeetWise Microphone is never used (see <see cref="AudioDeviceService.OpenCapture"/>).</param>
    public ClipRecorder(string micId, string? rememberedPhysicalId = null)
    {
        var device = AudioDeviceService.OpenCapture(micId, _enumerator, rememberedPhysicalId);
        _capture = new WasapiCapture(device);
        var fmt = _capture.WaveFormat;
        _resampler = new Resampler(fmt.SampleRate, fmt.Channels);
        _capture.DataAvailable += (_, e) =>
        {
            var f = LiveTranscriber.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            float peak = 0;
            foreach (var v in f) peak = Math.Max(peak, Math.Abs(v));
            Level?.Invoke(peak);
            var mono = _resampler.Process(f);
            lock (_gate) _samples.AddRange(mono);
        };
    }

    public void Start()
    {
        lock (_gate) _samples.Clear();
        _recording = true;
        _capture.StartRecording();
    }

    public float[] Stop()
    {
        if (_recording) { _recording = false; try { _capture.StopRecording(); } catch (InvalidOperationException) { } }
        lock (_gate) return _samples.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _enumerator.Dispose();
    }
}
