using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetWise.Speech;

namespace MeetWise.Audio;

/// <summary>Records a short voice sample (mono 16 kHz wav) used by voice isolation to lock onto the user.</summary>
public static class VoiceEnrollment
{
    public static string DefaultPath => Path.Combine(MeetWise.Core.AppPaths.Roaming, "voice", "me.wav");

    public static async Task RecordAsync(string micId, string outputWav, TimeSpan duration, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputWav)!);
        using var enumerator = new MMDeviceEnumerator();
        using var device = AudioDeviceService.OpenCapture(micId, enumerator);
        using var capture = new WasapiCapture(device);
        var fmt = capture.WaveFormat;
        var resampler = new Resampler(fmt.SampleRate, fmt.Channels);
        var samples = new List<float>();
        var gate = new object();
        capture.DataAvailable += (_, e) =>
        {
            var f = LiveTranscriber.ToFloat(e.Buffer, e.BytesRecorded, fmt.Encoding == WaveFormatEncoding.IeeeFloat);
            var mono16k = resampler.Process(f);
            lock (gate) samples.AddRange(mono16k);
        };
        capture.StartRecording();
        try { await Task.Delay(duration, ct); }
        finally { capture.StopRecording(); }

        float[] data;
        lock (gate) data = samples.ToArray();
        using var writer = new WaveFileWriter(outputWav, new WaveFormat(16000, 16, 1));
        writer.WriteSamples(data, 0, data.Length);
    }
}
