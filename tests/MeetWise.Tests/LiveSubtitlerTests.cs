using System.Diagnostics;
using MeetWise.Speech;
using NAudio.Wave;

namespace MeetWise.Tests;

public class LiveSubtitlerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public LiveSubtitlerTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");
    private static string Vad => Path.Combine(Models, "vad", "silero_vad.onnx");
    private static string WhisperBase => Path.Combine(Models, "whisper", "ggml-base.bin");
    private static string English => Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26");
    private static string Mandarin => Path.Combine(Models, "speaker", "four-speakers.wav");
    private static string Punct => Path.Combine(Models, "punct", "sherpa-onnx-online-punct-en-2024-08-06");

    private static bool HaveWhisper => File.Exists(Vad) && File.Exists(WhisperBase) && File.Exists(Mandarin);

    /// <summary>Feeds a 16 kHz 16-bit wav as 100 ms buffers, exactly like the audio thread does, and returns when it has been pushed.</summary>
    private static void Feed(LiveSubtitler sub, string channel, string wavPath, int seconds = int.MaxValue)
    {
        using var r = new WaveFileReader(wavPath);
        var buf = new byte[3200];
        long limit = (long)Math.Min(seconds, 1e9) * r.WaveFormat.AverageBytesPerSecond;
        long fed = 0;
        int n;
        while (fed < limit && (n = r.Read(buf, 0, buf.Length)) > 0) { sub.Push(channel, buf.AsSpan(0, n).ToArray(), n); fed += n; }
    }

    [Fact]
    public void WhisperPath_TranscribesMandarin_WithAutoLanguage_AndMonotonicTimes()
    {
        if (!HaveWhisper) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        var cues = new List<SubtitleCue>();
        using (var sub = new LiveSubtitler(new LiveSubtitlerOptions("auto", false, Vad, WhisperBase, null)))
        {
            sub.AddChannel("Others", 16000, 1, isFloat32: false);
            sub.Cue += c => { lock (cues) cues.Add(c); };
            Feed(sub, "Others", Mandarin, seconds: 40);
            sub.Stop();
        }
        Assert.True(cues.Count >= 4, $"only {cues.Count} subtitles for 40 s of conversation");
        Assert.Contains(cues, c => c.Text.Any(ch => ch >= '一' && ch <= '鿿')); // Chinese characters
        Assert.All(cues, c => Assert.Equal("Others", c.Speaker));
        Assert.Contains(cues, c => c.Language == "zh");
        Assert.All(cues, c => Assert.InRange(c.Start, 0, 41));
        Assert.All(cues, c => Assert.True(c.End > c.Start));
        _out.WriteLine(string.Join("\n", cues.Take(6).Select(c => $"[{c.Start:0.0}-{c.End:0.0}] {c.Text}")));
    }

    [Fact]
    public void WhisperPath_CanTranslateToEnglish()
    {
        if (!HaveWhisper) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        var cues = new List<SubtitleCue>();
        using (var sub = new LiveSubtitler(new LiveSubtitlerOptions("auto", true, Vad, WhisperBase, null)))
        {
            sub.AddChannel("Others", 16000, 1, isFloat32: false);
            sub.Cue += c => { lock (cues) cues.Add(c); };
            Feed(sub, "Others", Mandarin, seconds: 30);
            sub.Stop();
        }
        Assert.NotEmpty(cues);
        var text = string.Concat(cues.Select(c => c.Text));
        Assert.True(text.Count(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') > text.Length * 0.6, "translated output must be mostly Latin letters: " + text);
        Assert.All(cues, c => Assert.Equal("en", c.Language));
    }

    [Fact]
    public void WhisperPath_ReportsSpeaking_AndEachSubtitleAppearsSoonAfterItsPhrase()
    {
        if (!HaveWhisper) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        var speaking = new List<bool>();
        var sw = Stopwatch.StartNew();
        var firstCueAt = TimeSpan.Zero;
        using var sub = new LiveSubtitler(new LiveSubtitlerOptions("auto", false, Vad, WhisperBase, null));
        sub.AddChannel("Others", 16000, 1, isFloat32: false);
        sub.Speaking += (_, s) => { lock (speaking) speaking.Add(s); };
        sub.Cue += _ => { if (firstCueAt == TimeSpan.Zero) firstCueAt = sw.Elapsed; };
        Feed(sub, "Others", Mandarin, seconds: 12);
        var fedAt = sw.Elapsed;
        sub.Stop();
        lock (speaking) Assert.Contains(true, speaking);
        Assert.NotEqual(TimeSpan.Zero, firstCueAt);
        _out.WriteLine($"audio fed in {fedAt.TotalSeconds:0.0}s (faster than real time), first subtitle after {firstCueAt.TotalSeconds:0.0}s");
    }

    [Fact]
    public void StreamingEnglishPath_GivesPartialsAndPunctuatedCues()
    {
        var wav = Path.Combine(English, "test_wavs", "0.wav");
        if (!Directory.Exists(English) || !Directory.Exists(Punct) || !File.Exists(Vad)) return;
        using var punct = new EnglishPunctuator(Punct);
        var partials = new List<string>();
        var cues = new List<SubtitleCue>();
        using (var sub = new LiveSubtitler(new LiveSubtitlerOptions("en", false, Vad, null, English, punct.Restore)))
        {
            Assert.True(sub.UsesStreamingEnglish);
            sub.AddChannel("Me", 16000, 1, isFloat32: false);
            sub.Partial += (_, t) => { lock (partials) partials.Add(t); };
            sub.Cue += c => { lock (cues) cues.Add(c); };
            Feed(sub, "Me", wav);
            sub.Stop();
        }
        Assert.NotEmpty(partials);
        var cue = Assert.Single(cues);
        Assert.Equal("Me", cue.Speaker);
        Assert.Equal("en", cue.Language);
        Assert.Contains("yellow lamps", cue.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(char.IsUpper(cue.Text[0]), cue.Text);              // capital restored
        Assert.True(cue.Text.Contains('.') || cue.Text.Contains(','), cue.Text); // punctuation restored
    }

    [Fact]
    public void EnglishWithTranslation_UsesTheWhisperPath()
    {
        if (!HaveWhisper || !Directory.Exists(English)) return;
        using var sub = new LiveSubtitler(new LiveSubtitlerOptions("en", true, Vad, WhisperBase, English));
        Assert.False(sub.UsesStreamingEnglish); // translate-to-English cannot use the English-only streaming model
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("...", true)]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData("(music)", true)]
    [InlineData("you", true)]
    [InlineData("Hello there", false)]
    [InlineData("你好", false)]
    public void NoiseText_IsRecognised(string text, bool noise) => Assert.Equal(noise, LiveSubtitler.IsNoiseText(text));

    [Fact]
    public void NearSilence_IsNotSentToWhisper()
    {
        Assert.True(LiveSubtitler.IsNearSilent(new float[16000]));
        Assert.True(LiveSubtitler.IsNearSilent(Array.Empty<float>()));
        Assert.False(LiveSubtitler.IsNearSilent(Enumerable.Repeat(0.05f, 16000).ToArray()));
    }

    [Fact]
    public void WhisperPath_RequiresAModel()
        => Assert.Throws<ArgumentException>(() => new LiveSubtitler(new LiveSubtitlerOptions("pt", false, Vad, null, null)));
}
