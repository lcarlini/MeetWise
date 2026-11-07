using SherpaOnnx;

namespace MeetWise.Speech;

/// <summary>Voice-activity detection (Silero) for measuring how much of a recording is actually speech.</summary>
public static class SpeechDetector
{
    /// <summary>Speech segments of a mono 16 kHz clip as (startSec, endSec).</summary>
    public static IReadOnlyList<(double Start, double End)> Segments(float[] samples16k, string vadModel)
    {
        var cfg = new VadModelConfig();
        cfg.SileroVad.Model = vadModel;
        cfg.SileroVad.Threshold = 0.5f;
        cfg.SileroVad.MinSilenceDuration = 0.25f;
        cfg.SileroVad.MinSpeechDuration = 0.2f;
        cfg.SileroVad.WindowSize = 512;
        cfg.SampleRate = 16000;
        cfg.NumThreads = 1;
        using var vad = new VoiceActivityDetector(cfg, 60);

        var result = new List<(double, double)>();
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                result.Add((seg.Start / 16000.0, (seg.Start + seg.Samples.Length) / 16000.0));
                vad.Pop();
            }
        }
        const int window = 512;
        for (int i = 0; i + window <= samples16k.Length; i += window)
        {
            vad.AcceptWaveform(samples16k.AsSpan(i, window).ToArray());
            Drain();
        }
        vad.Flush();
        Drain();
        return result;
    }
}
