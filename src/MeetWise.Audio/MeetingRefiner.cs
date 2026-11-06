using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.Audio;

/// <summary>
/// Second pass over the recorded audio with Whisper: more accurate than the live recognizer, multilingual, and prompt-biased
/// with the user's custom vocabulary. Microphone and remote audio are transcribed separately so "Me" and "Others" stay correct.
/// </summary>
public static class MeetingRefiner
{
    public static async Task<int> RefineAsync(MeetingDatabase db, long meetingId, string? micWav, string? loopbackWav,
        WhisperTranscriber whisper, string language = "auto", IEnumerable<string>? vocabulary = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var channels = new List<(string Speaker, string Path)>();
        if (micWav is not null && File.Exists(micWav)) channels.Add(("Me", micWav));
        if (loopbackWav is not null && File.Exists(loopbackWav)) channels.Add((SpeakerLabeling.OthersChannel, loopbackWav));
        if (channels.Count == 0) throw new FileNotFoundException("The meeting's audio files were not found.");

        var all = new List<(string Speaker, double Start, double End, string Text, string? Language)>();
        for (int i = 0; i < channels.Count; i++)
        {
            var audio = await Task.Run(() => AudioFile.ReadMono16k(channels[i].Path), ct);
            if (IsSilent(audio)) continue; // an unused channel makes Whisper hallucinate text
            var share = new Progress<double>(p => progress?.Report((i + p) / channels.Count));
            foreach (var s in await whisper.TranscribeAsync(audio, language, vocabulary, progress: share, ct: ct))
                all.Add((channels[i].Speaker, s.Start, s.End, s.Text, s.Language));
        }

        var ordered = all.OrderBy(s => s.Start).ToList();
        // Never wipe a transcript because the second pass produced nothing.
        if (ordered.Count == 0) return 0;
        db.ReplaceSegments(meetingId, ordered);
        return ordered.Count;
    }

    /// <summary>True when the audio has no meaningful signal (RMS below about -60 dBFS).</summary>
    public static bool IsSilent(float[] samples)
    {
        if (samples.Length == 0) return true;
        double sum = 0;
        foreach (var v in samples) sum += v * v;
        return Math.Sqrt(sum / samples.Length) < 0.001;
    }
}
