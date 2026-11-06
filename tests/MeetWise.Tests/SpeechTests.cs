using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class SpeechTests
{
    [Fact]
    public void Resampler_48kStereo_To16kMono_HasRightLength()
    {
        var r = new Resampler(48000, 2);
        var block = new float[480 * 2];
        int total = 0;
        for (int i = 0; i < 100; i++) total += r.Process(block).Length;
        Assert.InRange(total, 15900, 16100);
    }

    [Fact]
    public void Resampler_PreservesDcLevel()
    {
        var r = new Resampler(44100, 1);
        var outp = r.Process(Enumerable.Repeat(0.5f, 4410).ToArray());
        Assert.All(outp, v => Assert.Equal(0.5f, v, 3));
    }

    [Fact]
    public void StreamingAsr_TranscribesSampleWav()
    {
        var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetWise", "models", "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26");
        if (!Directory.Exists(model)) return;
        using var reader = new WaveFileReader(Path.Combine(model, "test_wavs", "0.wav"));
        var sp = reader.ToSampleProvider();
        var all = new List<float>();
        var buf = new float[reader.WaveFormat.SampleRate / 10];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));

        var finals = new List<string>();
        using var rec = new StreamingRecognizer(model, "Me", reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        rec.Transcript += e => { if (e.IsFinal) finals.Add(e.Text); };
        var arr = all.ToArray();
        for (int i = 0; i < arr.Length; i += buf.Length)
            rec.Feed(arr.AsSpan(i, Math.Min(buf.Length, arr.Length - i)));
        rec.Finish();

        var text = string.Join(" ", finals).ToUpperInvariant();
        Assert.Contains("AFTER EARLY NIGHTFALL", text);
        Assert.Contains("BROTHELS", text);
    }
}

public class PipelineTests
{
    private static string ModelDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetWise", "models", "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26");

    [Fact]
    public void FileTranscriber_TranscribesWav()
    {
        if (!Directory.Exists(ModelDir)) return;
        var events = MeetWise.Audio.FileTranscriber.Transcribe(Path.Combine(ModelDir, "test_wavs", "1.wav"), ModelDir);
        Assert.Contains("DIRECT CONSEQUENCE", string.Join(" ", events.Select(e => e.Text)));
    }

    [Fact]
    public void LiveTranscriber_QueuesAndTranscribes_Int16Bytes()
    {
        if (!Directory.Exists(ModelDir)) return;
        using var reader = new NAudio.Wave.WaveFileReader(Path.Combine(ModelDir, "test_wavs", "0.wav"));
        var finals = new List<TranscriptEvent>();
        using var live = new LiveTranscriber(ModelDir);
        live.Transcript += e => { if (e.IsFinal) lock (finals) finals.Add(e); };
        live.AddChannel("Others", reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, isFloat32: false);
        var buf = new byte[3200];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0) live.Push("Others", buf, n);
        live.Stop();
        Assert.Contains("NIGHTFALL", string.Join(" ", finals.Select(e => e.Text)));
        Assert.All(finals, e => Assert.Equal("Others", e.Channel));
    }

    [Fact]
    public void ToFloat_ConvertsInt16()
    {
        var f = LiveTranscriber.ToFloat(new byte[] { 0x00, 0x40, 0x00, 0xC0 }, 4, false);
        Assert.Equal(0.5f, f[0], 3);
        Assert.Equal(-0.5f, f[1], 3);
    }
}
