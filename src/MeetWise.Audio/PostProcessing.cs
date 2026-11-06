using MeetWise.Speech;
using MeetWise.Storage;
using NAudio.Wave;

namespace MeetWise.Audio;

/// <summary>Reads any audio file Windows can decode and returns mono 16 kHz float samples.</summary>
public static class AudioFile
{
    public static float[] ReadMono16k(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var fmt = samples.WaveFormat;
        var rs = new Resampler(fmt.SampleRate, fmt.Channels);
        var all = new List<float>();
        var buf = new float[fmt.SampleRate * fmt.Channels];
        int n;
        while ((n = samples.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(rs.Process(buf.AsSpan(0, n)));
        return all.ToArray();
    }
}

/// <summary>After a meeting: split the remote channel into individual speakers and name the ones whose voices are enrolled.</summary>
public static class SpeakerLabeling
{
    public const string OthersChannel = "Others";

    /// <returns>The number of segments whose speaker label changed.</returns>
    public static int Apply(MeetingDatabase db, long meetingId, float[] remoteAudio16k, Diarizer diarizer, SpeakerProfiles? profiles = null)
    {
        var segments = db.GetTranscript(meetingId).Where(s => s.Speaker == OthersChannel).ToList();
        if (segments.Count == 0) return 0;
        var diar = diarizer.Diarize(remoteAudio16k);
        if (diar.Count == 0) return 0;

        var who = SpeakerAssigner.Assign(segments.Select(s => (s.StartSec, s.EndSec)).ToList(), diar);
        var names = DiarizedNames(diar, remoteAudio16k, profiles);

        int changed = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (who[i] < 0) continue;
            db.SetSegmentSpeaker(segments[i].Id, names[who[i]]);
            changed++;
        }
        return changed;
    }

    /// <summary>"Speaker 1", "Speaker 2" in order of first appearance, replaced by an enrolled name when the voice matches.</summary>
    public static Dictionary<int, string> DiarizedNames(IReadOnlyList<DiarSegment> diar, float[] audio16k, SpeakerProfiles? profiles)
    {
        var order = diar.Select(d => d.Speaker).Distinct().ToList();
        var names = new Dictionary<int, string>();
        for (int i = 0; i < order.Count; i++)
        {
            string label = $"Speaker {i + 1}";
            if (profiles is not null)
            {
                // Use up to 20 s of this speaker's longest turns as the sample to identify.
                var samples = new List<float>();
                foreach (var d in diar.Where(d => d.Speaker == order[i]).OrderByDescending(d => d.End - d.Start))
                {
                    int a = Math.Max(0, (int)(d.Start * 16000)), b = Math.Min(audio16k.Length, (int)(d.End * 16000));
                    if (b > a) samples.AddRange(audio16k.AsSpan(a, b - a).ToArray());
                    if (samples.Count >= 16000 * 20) break;
                }
                if (samples.Count >= 16000 * 3 && profiles.Identify(samples.ToArray()) is { } known) label = known;
            }
            names[order[i]] = label;
        }
        return names;
    }
}
