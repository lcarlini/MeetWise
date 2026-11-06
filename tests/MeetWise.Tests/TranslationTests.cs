using MeetWise.Intelligence;
using MeetWise.Speech;

namespace MeetWise.Tests;

public class TranslationTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    [Fact]
    public async Task Translator_PromptsForTargetLanguage_Caches_AndCleansReply()
    {
        var llm = new FakeLlm { Reply = _ => "  \"Olá, mundo\"  " };
        var t = new LlmTranslator(llm);
        Assert.Equal("Olá, mundo", await t.TranslateAsync("Hello, world", "pt", "en"));
        Assert.Equal("Olá, mundo", await t.TranslateAsync("Hello, world", "pt", "en"));
        Assert.Single(llm.Prompts);
        Assert.Contains("Portuguese", llm.Prompts[0].System);
        Assert.Contains("from English", llm.Prompts[0].System);
        Assert.Equal("", await t.TranslateAsync("   ", "pt"));
    }

    [Fact]
    public async Task WindowsVoice_Speech_IsAudible_AndRecognizableByAsr()
    {
        var asr = Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26");
        if (!Directory.Exists(asr)) return;
        using var voice = new MeetWise.Audio.WindowsVoice();
        if (!voice.SelectLanguage("en")) return; // no English voice installed on this PC

        var (audio, rate) = await voice.SynthesizeAsync("The meeting starts at nine tomorrow morning.");
        Assert.True(audio.Length > rate, "expected more than a second of speech");
        Assert.False(MeetWise.Audio.MeetingRefiner.IsSilent(audio));

        var finals = new List<string>();
        using var rec = new StreamingRecognizer(asr, "tts", rate, 1);
        rec.Transcript += e => { if (e.IsFinal) finals.Add(e.Text); };
        for (int i = 0; i < audio.Length; i += rate / 10) rec.Feed(audio.AsSpan(i, Math.Min(rate / 10, audio.Length - i)));
        rec.Finish();
        var text = string.Join(" ", finals).ToUpperInvariant();
        Assert.Contains("MEETING", text);
        Assert.Contains("NINE", text);
        Assert.Contains("MORN", text);
    }

    [Fact]
    public async Task WindowsVoice_ReportsInstalledLanguages_AndFallsBackWhenMissing()
    {
        Assert.NotEmpty(MeetWise.Audio.WindowsVoice.InstalledLanguages);
        using var voice = new MeetWise.Audio.WindowsVoice();
        Assert.False(voice.SelectLanguage("xx"));
        await Task.CompletedTask;
    }
}
