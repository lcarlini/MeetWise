using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Audio;

/// <summary>Transcribes existing audio or video files (anything Windows Media Foundation can decode).</summary>
public static class FileTranscriber
{
    public static IReadOnlyList<TranscriptEvent> Transcribe(string path, string modelDir, string speaker = "Speaker",
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var finals = new List<TranscriptEvent>();
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var fmt = samples.WaveFormat;
        using var rec = new StreamingRecognizer(modelDir, speaker, fmt.SampleRate, fmt.Channels, threads: Math.Max(2, Environment.ProcessorCount / 2));
        rec.Transcript += e => { if (e.IsFinal) finals.Add(e); };

        var buf = new float[fmt.SampleRate * fmt.Channels / 5];
        int n;
        while ((n = samples.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            rec.Feed(buf.AsSpan(0, n));
            if (reader.TotalTime.TotalSeconds > 0)
                progress?.Report(reader.CurrentTime.TotalSeconds / reader.TotalTime.TotalSeconds);
        }
        rec.Finish();
        return finals;
    }
}
