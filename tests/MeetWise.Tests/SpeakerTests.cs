using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class SpeakerTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");
    private static string Seg => Path.Combine(Models, "speaker", "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
    private static string Emb => Path.Combine(Models, "speaker", "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
    private static string Four => Path.Combine(Models, "speaker", "four-speakers.wav");
    private static bool Have => File.Exists(Seg) && File.Exists(Emb) && File.Exists(Four);

    private static float[] Wav(string path)
    {
        using var r = new WaveFileReader(path);
        var sp = r.ToSampleProvider();
        var all = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return all.ToArray();
    }

    [Fact]
    public void Diarizer_FindsFourSpeakers_InKnownRecording()
    {
        if (!Have) return;
        using var d = new Diarizer(Seg, Emb);
        var segs = d.Diarize(Wav(Four));
        var speakers = segs.Select(s => s.Speaker).Distinct().ToList();
        Assert.InRange(speakers.Count, 3, 5);
        Assert.True(segs.Sum(s => s.End - s.Start) > 30, "expected a lot of speech in the sample");
        Assert.True(segs.Zip(segs.Skip(1)).All(p => p.First.Start <= p.Second.Start), "segments must be sorted");
    }

    [Fact]
    public void Diarizer_RespectsKnownSpeakerCount()
    {
        if (!Have) return;
        using var d = new Diarizer(Seg, Emb, numSpeakers: 2);
        Assert.Equal(2, d.Diarize(Wav(Four)).Select(s => s.Speaker).Distinct().Count());
    }

    [Fact]
    public void Assigner_PicksLargestOverlap()
    {
        var diar = new[] { new DiarSegment(0, 5, 0), new DiarSegment(5, 12, 1), new DiarSegment(12, 20, 0) };
        var who = SpeakerAssigner.Assign(new (double, double)[] { (1, 4), (4, 9), (11, 19), (30, 31) }, diar);
        Assert.Equal(new[] { 0, 1, 0, -1 }, who);
    }

    [Fact]
    public void Profiles_RecognizeSameVoice_NotOthers_AndPersist()
    {
        if (!File.Exists(Emb)) return;
        var asr = Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs");
        if (!Directory.Exists(asr)) return;
        var long1 = Wav(Path.Combine(asr, "1.wav"));          // ~17 s, one speaker
        var other = Wav(Path.Combine(asr, "0.wav"));          // a different speaker
        int half = long1.Length / 2;
        var store = Path.Combine(Path.GetTempPath(), "mw-voices-" + Guid.NewGuid().ToString("N"), "voices.json");

        using (var p = new SpeakerProfiles(Emb, store))
        {
            p.Enroll("Ana", long1[..half]);
            Assert.Equal("Ana", p.Identify(long1[half..]));
            Assert.Null(p.Identify(other));
        }
        using var reloaded = new SpeakerProfiles(Emb, store);
        Assert.Contains("Ana", reloaded.Names);
        Assert.Equal("Ana", reloaded.Identify(long1[half..]));
        reloaded.Forget("Ana");
        Assert.Null(reloaded.Identify(long1[half..]));
    }
}
