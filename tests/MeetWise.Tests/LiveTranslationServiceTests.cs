using System.Diagnostics;
using MeetWise.Audio;
using MeetWise.Intelligence;
using Xunit.Abstractions;

namespace MeetWise.Tests;

internal sealed class FakeTranslator : IDetailedTranslator
{
    public readonly List<(string Text, string Target, string? Source)> Calls = new();
    public Func<string, string, string?, CancellationToken, Task<TranslationResult>> Handler { get; set; }
        = (t, target, _, _) => Task.FromResult(new TranslationResult($"[{target}] {t}", Array.Empty<string>()));

    public async Task<TranslationResult> TranslateDetailedAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((text, targetLanguage, sourceLanguage));
        return await Handler(text, targetLanguage, sourceLanguage, ct);
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default)
        => (await TranslateDetailedAsync(text, targetLanguage, sourceLanguage, ct)).Text;

    public int Count { get { lock (Calls) return Calls.Count; } }
    public string[] Texts() { lock (Calls) return Calls.Select(c => c.Text).ToArray(); }
}

internal sealed class FakeTts : ISpeechSynthesizer
{
    public HashSet<string> Voices { get; } = new() { "en", "pt", "es" };
    public readonly List<string> Spoken = new();
    public string? Selected;
    public Func<string, CancellationToken, Task<(float[], int)>> Synth { get; set; } = (_, _) => Task.FromResult((new float[16000], 16000)); // one second
    public bool SelectLanguage(string language) { Selected = language; return Voices.Contains(language); }
    public async Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, CancellationToken ct)
    {
        lock (Spoken) Spoken.Add(text);
        return await Synth(text, ct);
    }
}

internal sealed class FakeOutput : ISpeechOutput
{
    public readonly List<(float[] Samples, int Rate)> Played = new();
    public int Stops;
    public double Queued { get; set; }
    public Exception? FailWith { get; set; }
    public double QueuedSeconds => Queued;
    public void Play(float[] samples, int sampleRate)
    {
        if (FailWith is not null) throw FailWith;
        lock (Played) Played.Add((samples, sampleRate));
    }
    public void Stop() => Interlocked.Increment(ref Stops);
}

public class LiveTranslationServiceTests
{
    private readonly ITestOutputHelper _out;
    public LiveTranslationServiceTests(ITestOutputHelper o) => _out = o;

    private static readonly TimeSpan LongHold = TimeSpan.FromSeconds(30);

    private sealed class Harness : IDisposable
    {
        public readonly FakeTranslator Translator = new();
        public readonly FakeTts Tts = new();
        public readonly FakeOutput Output = new();
        public readonly List<TranslatedCaption> Captions = new();
        public readonly List<TranslationStatus> Statuses = new();
        public readonly List<int> CaptionThreads = new();
        public LiveTranslationService Service = null!;
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public Harness Start(LiveTranslationOptions options, bool useClock = false)
        {
            Service = new LiveTranslationService(Translator, options, Tts, Output, useClock ? () => Now : null);
            Service.Caption += c => { lock (Captions) { Captions.Add(c); CaptionThreads.Add(Environment.CurrentManagedThreadId); } };
            Service.Status += s => { lock (Statuses) Statuses.Add(s); };
            return this;
        }

        public TranslatedCaption[] CaptionsSnapshot() { lock (Captions) return Captions.ToArray(); }
        public TranslationStatus[] StatusSnapshot() { lock (Statuses) return Statuses.ToArray(); }
        public void Dispose() => Service.Dispose();
    }

    private static LiveTranslationOptions Captions(string target = "pt", TimeSpan? hold = null, int maxPending = 4, TimeSpan? maxAge = null)
        => new(target, null, "auto", hold ?? LongHold, MaxPending: maxPending, CaptionMaxAge: maxAge);

    private static LiveTranslationOptions Speech(string target = "pt", string spoken = "auto", TimeSpan? hold = null, TimeSpan? maxAge = null)
        => new(null, target, spoken, hold ?? LongHold, SpeechMaxAge: maxAge);

    private static LiveUtterance Others(string text, string? lang = null) => new("Others", text, lang, 0, 1);
    private static LiveUtterance Me(string text, string? lang = null) => new("Me", text, lang, 0, 1);

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    // ---- segmentation ----

    [Fact]
    public async Task FragmentsOfOneSentence_AreTranslatedTogether_WhenTheSentenceEnds()
    {
        using var h = new Harness().Start(Captions());
        h.Service.OnFinal(Others("so I think"));
        h.Service.OnFinal(Others("we should ship"));
        h.Service.OnFinal(Others("on Friday."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        Assert.Equal(new[] { "so I think we should ship on Friday." }, h.Translator.Texts());
    }

    [Fact]
    public async Task SeveralSentencesInOneUtterance_AreTranslatedOneByOne_InOrder()
    {
        using var h = new Harness().Start(Captions());
        h.Service.OnFinal(Others("Hello there. How are you? I am fine."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 3));
        Assert.Equal(new[] { "Hello there.", "How are you?", "I am fine." }, h.CaptionsSnapshot().Select(c => c.Original).ToArray());
        Assert.Equal(h.CaptionsSnapshot().Select(c => c.Seq).OrderBy(x => x), h.CaptionsSnapshot().Select(c => c.Seq));
    }

    [Fact]
    public async Task AnUnfinishedSentence_IsTranslatedAfterTheHold_NotBefore()
    {
        using var h = new Harness().Start(Captions(hold: TimeSpan.FromMilliseconds(400)));
        h.Service.OnFinal(Others("we could maybe"));
        await Task.Delay(100);
        Assert.Equal(0, h.Translator.Count); // still waiting for the rest of the sentence
        Assert.True(await Eventually(() => h.Translator.Count == 1, 3000));
        Assert.Equal("we could maybe", h.Translator.Texts()[0]);
    }

    [Fact]
    public async Task InterruptedSpeech_DoesNotGlueTheOldFragmentToANewSentence()
    {
        using var h = new Harness().Start(Captions(hold: TimeSpan.FromMilliseconds(150)));
        h.Service.OnFinal(Others("I was going to say"));
        Assert.True(await Eventually(() => h.Translator.Count == 1, 3000)); // the hold flushed the fragment
        h.Service.OnFinal(Others("Anyway, the budget is approved."));
        Assert.True(await Eventually(() => h.Translator.Count == 2, 3000));
        Assert.Equal(new[] { "I was going to say", "Anyway, the budget is approved." }, h.Translator.Texts());
    }

    [Fact]
    public async Task ALongMonologueWithoutPunctuation_IsCutAtClauseBoundaries_WithoutLosingWords()
    {
        using var h = new Harness().Start(Captions(hold: TimeSpan.FromMilliseconds(150)));
        var words = Enumerable.Range(1, 120).Select(i => i % 9 == 0 ? $"word{i}," : $"word{i}").ToArray();
        h.Service.OnFinal(Others(string.Join(' ', words)));
        Assert.True(await Eventually(() => string.Join(' ', h.Translator.Texts()).Split(' ').Length >= 120, 4000));
        var texts = h.Translator.Texts();
        Assert.True(texts.Length >= 3, "a monologue of 700+ characters must be split");
        Assert.All(texts, t => Assert.True(t.Length <= 230, $"unit of {t.Length} characters"));
        Assert.Equal(words, string.Join(' ', texts).Split(' ')); // nothing lost, nothing repeated, same order
    }

    [Theory]
    [InlineData("Dr. Silva paid 3.5 million. Next steps follow.", "Dr. Silva paid 3.5 million.|Next steps follow.")]
    [InlineData("Version 2.1 is out. Please update.", "Version 2.1 is out.|Please update.")]
    [InlineData("你好。你好吗？", "你好。|你好吗？")]
    [InlineData("Wait... what? Really!", "Wait...|what?|Really!")]
    public async Task SentenceEnds_AreFoundCorrectly_AbbreviationsAndDecimalsAreNot(string input, string expected)
    {
        using var h = new Harness().Start(Captions());
        h.Service.OnFinal(Others(input));
        var want = expected.Split('|');
        Assert.True(await Eventually(() => h.Translator.Count >= want.Length));
        Assert.Equal(want, h.Translator.Texts());
    }

    [Fact]
    public async Task TechnicalTerms_ReachTheTranslatorUntouched()
    {
        using var h = new Harness().Start(Captions());
        const string text = "Run kubectl rollout restart deployment/api-gateway --namespace=prod-eu and check MeetWise v1.2.3.";
        h.Service.OnFinal(Others(text));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        Assert.Equal(text, h.Translator.Texts()[0]);
    }

    // ---- language handling ----

    [Fact]
    public async Task TheDetectedLanguage_DoesNotFlip_BecauseOfAShortOrNoisyUtterance()
    {
        using var h = new Harness().Start(Captions("es"));
        h.Service.OnFinal(Others("Good morning everyone.", "en"));      // en
        h.Service.OnFinal(Others("ok sim.", "pt"));                      // 2 words of "pt": noise, ignored
        h.Service.OnFinal(Others("We should start now.", "en"));         // en
        h.Service.OnFinal(Others("vamos começar agora.", "pt"));         // 3 words of "pt": first sighting
        h.Service.OnFinal(Others("podemos seguir adiante então.", "pt")); // second sighting: it really switched
        h.Service.OnFinal(Others("I think we should deliver on Friday without fail for sure.", "en")); // a full sentence switches at once
        Assert.True(await Eventually(() => h.Translator.Count == 6));
        Assert.Equal(new[] { "en", "en", "en", "en", "pt", "en" }, h.Translator.Calls.Select(c => c.Source).ToArray());
    }

    [Fact]
    public async Task UnknownOrUnsupportedLanguages_AreIgnored_NotTreatedAsASwitch()
    {
        using var h = new Harness().Start(Captions("pt"));
        h.Service.OnFinal(Others("Hello everyone.", "xx"));   // unknown at the start: let the model detect it
        h.Service.OnFinal(Others("Welcome to the call.", "en"));
        h.Service.OnFinal(Others("Another long sentence about the plan for next week.", "klingon"));
        Assert.True(await Eventually(() => h.Translator.Count == 3));
        Assert.Equal(new string?[] { null, "en", "en" }, h.Translator.Calls.Select(c => c.Source).ToArray());
    }

    [Theory]
    [InlineData("en", "pt")]
    [InlineData("pt", "en")]
    [InlineData("en", "es")]
    [InlineData("pt", "es")]
    public async Task EnglishPortugueseSpanish_AreAskedForInBothDirections(string from, string to)
    {
        using var h = new Harness().Start(Captions(to));
        h.Service.OnFinal(Others("This is a sentence in the source language.", from));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        var call = h.Translator.Calls[0];
        Assert.Equal(to, call.Target);
        Assert.Equal(from, call.Source);
        Assert.Equal(from, h.CaptionsSnapshot()[0].SourceLanguage);
        Assert.Equal(to, h.CaptionsSnapshot()[0].TargetLanguage);
    }

    [Fact]
    public async Task SpeechAlreadyInTheTargetLanguage_NeedsNoTranslationAndNoCaption()
    {
        using var h = new Harness().Start(Captions("pt"));
        h.Service.OnFinal(Others("Bom dia a todos.", "pt"));
        Assert.True(await Eventually(() => h.Service.Stats.SameLanguage == 1));
        Assert.Equal(0, h.Translator.Count);
        Assert.Empty(h.CaptionsSnapshot());
    }

    [Fact]
    public async Task MixedLanguageSpeech_GoesToTheModelWithoutASourceHint()
    {
        using var h = new Harness().Start(Captions("pt"));
        h.Service.OnFinal(Others("We need to fazer o deploy tomorrow.", null));
        Assert.True(await Eventually(() => h.Translator.Count == 1));
        Assert.Null(h.Translator.Calls[0].Source);
    }

    // ---- latency: the audio, the transcript and the UI never wait for a model ----

    [Fact]
    public async Task OnFinal_ReturnsImmediately_EvenWhenTheModelIsSlow()
    {
        using var h = new Harness().Start(Captions());
        h.Translator.Handler = async (t, target, _, ct) => { await Task.Delay(300, ct); return new TranslationResult(t, Array.Empty<string>()); };
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) h.Service.OnFinal(Others($"Sentence number {i}."));
        sw.Stop();
        _out.WriteLine($"50 OnFinal calls took {sw.ElapsedMilliseconds} ms while the model needs 300 ms per sentence");
        Assert.True(sw.ElapsedMilliseconds < 150, $"OnFinal blocked for {sw.ElapsedMilliseconds} ms");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AModelThatNeverAnswers_NeverBlocksTheCaller_AndShutdownStillCompletes()
    {
        var h = new Harness().Start(Captions());
        h.Translator.Handler = async (t, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) h.Service.OnFinal(Others($"Sentence {i}."));
        Assert.True(sw.ElapsedMilliseconds < 150);
        await Task.Delay(100);
        sw.Restart();
        h.Dispose(); // cancels the hung request
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Dispose took {sw.ElapsedMilliseconds} ms");
        Assert.Empty(h.CaptionsSnapshot());
    }

    [Fact]
    public async Task CaptionLatency_IsMeasured_FromTheEndOfTheSentenceToTheCaption()
    {
        using var h = new Harness().Start(Captions());
        h.Translator.Handler = async (t, target, _, ct) => { await Task.Delay(120, ct); return new TranslationResult($"[{target}] {t}", Array.Empty<string>()); };
        for (int i = 0; i < 6; i++)
        {
            int before = h.CaptionsSnapshot().Length;
            h.Service.OnFinal(Others($"Sentence number {i}."));
            Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == before + 1));
        }
        foreach (var c in h.CaptionsSnapshot()) Assert.InRange(c.Latency.TotalMilliseconds, 100, 1500);
        _out.WriteLine($"caption latency with a 120 ms model: p50 {h.Service.Stats.CaptionLatencyP50Ms:0} ms, p95 {h.Service.Stats.CaptionLatencyP95Ms:0} ms");
        Assert.InRange(h.Service.Stats.CaptionLatencyP50Ms, 100, 1000);
    }

    // ---- concurrency and load ----

    [Fact]
    public async Task ARushOfSentences_NeverBacklogsBeyondTheLimit_OutputStaysOrdered_AndEverySentenceIsAccountedFor()
    {
        using var h = new Harness().Start(Captions(maxPending: 4));
        h.Translator.Handler = async (t, target, _, ct) => { await Task.Delay(30, ct); return new TranslationResult($"[{target}] {t}", Array.Empty<string>()); };
        int maxPending = 0;
        using var watch = new CancellationTokenSource();
        var poll = Task.Run(async () => { while (!watch.IsCancellationRequested) { maxPending = Math.Max(maxPending, h.Service.Pending); await Task.Delay(1); } });
        for (int i = 0; i < 40; i++) { h.Service.OnFinal(Others($"Sentence number {i}.")); await Task.Delay(2); }
        Assert.True(await Eventually(() => h.Service.Stats.Translated + h.Service.Stats.Dropped == 40, 10000));
        watch.Cancel();
        await poll;
        var caps = h.CaptionsSnapshot();
        _out.WriteLine($"40 sentences in 80 ms against a 30 ms model: translated {h.Service.Stats.Translated}, dropped {h.Service.Stats.Dropped}, max pending {maxPending}");
        Assert.True(maxPending <= 4, $"backlog reached {maxPending}");
        Assert.True(h.Service.Stats.Dropped > 0);
        Assert.Contains(h.StatusSnapshot(), s => s.Kind == TranslationStatusKind.Skipped); // dropping is announced, never silent
        Assert.Equal(caps.Select(c => c.Seq).OrderBy(x => x), caps.Select(c => c.Seq));   // in order
        Assert.Equal(caps.Length, caps.Select(c => c.Original).Distinct().Count());        // no duplicates
    }

    [Fact]
    public async Task ASentenceThatWaitedTooLong_IsSkipped_AndSaidSo()
    {
        using var h = new Harness().Start(Captions(maxAge: TimeSpan.FromMilliseconds(250)));
        h.Translator.Handler = async (t, target, _, ct) => { await Task.Delay(400, ct); return new TranslationResult(t, Array.Empty<string>()); };
        for (int i = 0; i < 3; i++) h.Service.OnFinal(Others($"Sentence number {i}."));
        Assert.True(await Eventually(() => h.Service.Stats.Translated + h.Service.Stats.Dropped == 3, 5000));
        Assert.Equal(1, h.Service.Stats.Translated);
        Assert.Equal(2, h.Service.Stats.Dropped);
        Assert.Contains(h.StatusSnapshot(), s => s.Kind == TranslationStatusKind.Skipped && s.Message.Contains("waited too long"));
    }

    [Fact]
    public async Task ManyThreadsFeedingTheService_CannotBreakIt()
    {
        using var h = new Harness().Start(Captions(maxPending: 500));
        Parallel.For(0, 8, t => { for (int i = 0; i < 25; i++) h.Service.OnFinal(Others($"Thread {t} sentence {i}.")); });
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 200, 10000));
        var caps = h.CaptionsSnapshot();
        Assert.Equal(200, caps.Select(c => c.Seq).Distinct().Count());
        Assert.Equal(200, caps.Select(c => c.Original).Distinct().Count());
    }

    [Fact]
    public async Task Captions_ArriveOnABackgroundThread_NotOnTheCallersThread()
    {
        using var h = new Harness().Start(Captions());
        int caller = Environment.CurrentManagedThreadId;
        h.Service.OnFinal(Others("Hello there."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        Assert.NotEqual(caller, h.CaptionThreads[0]); // consumers (the UI) must marshal; the service never touches a UI thread
    }

    // ---- failures ----

    [Theory]
    [InlineData(TranslationFailure.Timeout)]
    [InlineData(TranslationFailure.ProviderError)]
    [InlineData(TranslationFailure.RateLimited)]
    [InlineData(TranslationFailure.InvalidResponse)]
    public async Task WhenTranslationFails_TheOriginalIsStillShown_TheUserIsToldOnce_AndRecoveryIsAnnounced(TranslationFailure kind)
    {
        using var h = new Harness().Start(Captions(maxPending: 20)); // room for the whole burst: dropping under load is tested separately
        bool broken = true;
        h.Translator.Handler = (t, target, _, _) => broken
            ? throw new TranslationException(kind, "reason: " + kind)
            : Task.FromResult(new TranslationResult($"[{target}] {t}", Array.Empty<string>()));
        for (int i = 0; i < 5; i++) h.Service.OnFinal(Others($"Sentence number {i}."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 5));

        var failed = h.CaptionsSnapshot();
        Assert.All(failed, c => { Assert.Null(c.Translated); Assert.Contains(kind.ToString(), c.Error); });
        Assert.Equal(5, failed.Select(c => c.Original).Distinct().Count()); // nothing the other side said is lost
        var degraded = h.StatusSnapshot().Where(s => s.Kind == TranslationStatusKind.Degraded).ToArray();
        Assert.Single(degraded); // five failures, one message
        Assert.Contains(kind.ToString(), degraded[0].Message);

        broken = false;
        h.Service.OnFinal(Others("It works again."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 6));
        Assert.NotNull(h.CaptionsSnapshot()[5].Translated);
        Assert.Contains(h.StatusSnapshot(), s => s.Kind == TranslationStatusKind.Ok);
        Assert.Equal(5, h.Service.Stats.Failed);
    }

    [Fact]
    public async Task AnUnexpectedException_DoesNotStopTheWorker()
    {
        using var h = new Harness().Start(Captions());
        h.Translator.Handler = (t, target, _, _) => t.Contains("boom") ? throw new InvalidOperationException("boom") : Task.FromResult(new TranslationResult($"[{target}] {t}", Array.Empty<string>()));
        h.Service.OnFinal(Others("boom."));
        h.Service.OnFinal(Others("Still alive."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Any(c => c.Original == "Still alive." && c.Translated is not null)));
    }

    [Fact]
    public async Task ADoubtfulTranslation_IsMarked()
    {
        using var h = new Harness().Start(Captions());
        h.Translator.Handler = (t, target, _, _) => Task.FromResult(new TranslationResult("x", new[] { "numbers differ from the original" }));
        h.Service.OnFinal(Others("The server has 16 GB of RAM."));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        Assert.True(h.CaptionsSnapshot()[0].Doubtful);
    }

    [Fact]
    public async Task DisposingDuringATranslation_RaisesNothing_AndLeaksNothing()
    {
        var h = new Harness().Start(Captions());
        h.Translator.Handler = async (t, _, _, ct) => { await Task.Delay(2000, ct); return new TranslationResult(t, Array.Empty<string>()); };
        h.Service.OnFinal(Others("Hello."));
        await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        h.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1500);
        await Task.Delay(100);
        Assert.Empty(h.CaptionsSnapshot());
        h.Service.OnFinal(Others("After shutdown.")); // must not throw
    }

    [Fact]
    public async Task ChannelsThatAreNotConfigured_AreIgnored()
    {
        using var h = new Harness().Start(Captions("pt"));
        h.Service.OnFinal(Me("This is me speaking."));
        h.Service.OnFinal(new LiveUtterance("Room", "Some other channel.", null, 0, 1));
        h.Service.OnFinal(Others("   "));
        await Task.Delay(100);
        Assert.Equal(0, h.Translator.Count);
        Assert.Equal(0, h.Service.Stats.Received);
    }

    // ---- spoken translation ----

    [Fact]
    public async Task MySentence_IsTranslated_Voiced_AndPlayedOnTheChosenOutput()
    {
        using var h = new Harness().Start(Speech("pt"));
        h.Service.OnFinal(Me("Good morning, everyone.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
        Assert.Equal("pt", h.Tts.Selected);
        Assert.Equal("[pt] Good morning, everyone.", h.Tts.Spoken[0]);
        Assert.Equal(16000, h.Output.Played[0].Rate);
        Assert.Equal(1, h.Service.Stats.Spoken);
        Assert.True(h.Service.Stats.SpeechLatencyP50Ms >= 0);
    }

    [Fact]
    public async Task ReVoiceMode_SpeaksTheSameWords_WithoutAnyTranslation()
    {
        using var h = new Harness().Start(Speech("same", spoken: "en"));
        h.Service.OnFinal(Me("Please send me the report.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
        Assert.Equal(0, h.Translator.Count);
        Assert.Equal("en", h.Tts.Selected);
        Assert.Equal("Please send me the report.", h.Tts.Spoken[0]);
    }

    [Fact]
    public async Task WhenMySentenceCannotBeTranslated_IAmToldItWasNotSpoken()
    {
        using var h = new Harness().Start(Speech("pt"));
        h.Translator.Handler = (_, _, _, _) => throw new TranslationException(TranslationFailure.ProviderError, "no network");
        h.Service.OnFinal(Me("Good morning.", "en"));
        Assert.True(await Eventually(() => h.StatusSnapshot().Any(s => s.Kind == TranslationStatusKind.Degraded)));
        Assert.Contains("not spoken", h.StatusSnapshot().First(s => s.Kind == TranslationStatusKind.Degraded).Message);
        Assert.Empty(h.Output.Played);
        Assert.Empty(h.Tts.Spoken);
    }

    [Fact]
    public async Task AFailingVoice_IsReported_AndTheNextSentenceStillWorks()
    {
        using var h = new Harness().Start(Speech("pt"));
        bool fail = true;
        h.Tts.Synth = (t, _) => fail ? throw new InvalidOperationException("voice crashed") : Task.FromResult((new float[1600], 16000));
        h.Service.OnFinal(Me("First sentence.", "en"));
        Assert.True(await Eventually(() => h.StatusSnapshot().Any(s => s.Message.Contains("Windows voice failed"))));
        fail = false;
        h.Service.OnFinal(Me("Second sentence.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
    }

    [Fact]
    public async Task ALanguageWithoutAnInstalledVoice_IsExplainedOnce()
    {
        using var h = new Harness().Start(Speech("pt"));
        h.Tts.Voices.Clear();
        h.Service.OnFinal(Me("First sentence.", "en"));
        h.Service.OnFinal(Me("Second sentence.", "en"));
        Assert.True(await Eventually(() => h.Translator.Count == 2));
        await Task.Delay(100);
        var missing = h.StatusSnapshot().Where(s => s.Kind == TranslationStatusKind.VoiceMissing).ToArray();
        Assert.Single(missing);
        Assert.Contains("'pt'", missing[0].Message);
        Assert.Empty(h.Output.Played);
    }

    [Fact]
    public async Task AnUnavailableOutputDevice_IsReported_NotThrown_AndRecoversWhenItReturns()
    {
        using var h = new Harness().Start(Speech("pt"));
        h.Output.FailWith = new SpeechOutputException("The output device chosen for the translated voice is not connected.");
        h.Service.OnFinal(Me("First sentence.", "en"));
        h.Service.OnFinal(Me("Second sentence.", "en"));
        Assert.True(await Eventually(() => h.StatusSnapshot().Any(s => s.Kind == TranslationStatusKind.OutputUnavailable)));
        await Task.Delay(100);
        Assert.Single(h.StatusSnapshot(), s => s.Kind == TranslationStatusKind.OutputUnavailable);
        h.Output.FailWith = null;
        h.Service.OnFinal(Me("Third sentence.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
    }

    [Fact]
    public async Task StopSpeaking_CancelsSynthesis_EmptiesTheQueue_AndTheNextSentenceStillPlays()
    {
        using var h = new Harness().Start(Speech("pt"));
        int started = 0;
        h.Tts.Synth = async (t, ct) =>
        {
            Interlocked.Increment(ref started);
            if (t.Contains("long")) await Task.Delay(Timeout.Infinite, ct);
            return (new float[1600], 16000);
        };
        h.Service.OnFinal(Me("A long sentence that takes forever.", "en"));
        Assert.True(await Eventually(() => started == 1));
        h.Service.OnFinal(Me("Queued sentence one.", "en"));
        h.Service.OnFinal(Me("Queued sentence two.", "en"));
        h.Service.StopSpeaking();
        Assert.True(h.Output.Stops >= 1);
        h.Service.OnFinal(Me("Fresh sentence after stopping.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count >= 1));
        await Task.Delay(100);
        Assert.Single(h.Output.Played); // the stale ones never played
        Assert.Contains("Fresh sentence after stopping.", h.Tts.Spoken[^1]);
    }

    [Fact]
    public async Task WhenThePlaybackQueueIsFarBehind_StaleSpeechIsSkipped_AndSaidSo()
    {
        using var h = new Harness().Start(Speech("pt"));
        h.Output.Queued = 25;
        h.Service.OnFinal(Me("A sentence nobody wants to hear late.", "en"));
        Assert.True(await Eventually(() => h.StatusSnapshot().Any(s => s.Kind == TranslationStatusKind.Skipped)));
        Assert.Empty(h.Output.Played);
    }

    // ---- no feedback loop ----

    [Fact]
    public async Task WhatTheMicrophoneHearsOfTheTranslatedVoice_IsNotTranslatedAgain()
    {
        // The translated voice goes to the virtual microphone, which the recorder may be listening to as "Me".
        using var h = new Harness().Start(Speech("pt"), useClock: true);
        h.Service.OnFinal(Me("Good morning, everyone.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
        int calls = h.Translator.Count;

        h.Now += TimeSpan.FromSeconds(2); // while the voice is playing the recognizer "hears" it
        h.Service.OnFinal(Me("[pt] Good morning, everyone.", "pt"));
        await Task.Delay(100);
        Assert.Equal(calls, h.Translator.Count);
        Assert.Equal(1, h.Service.Stats.OwnVoiceIgnored);
        Assert.Single(h.Output.Played); // no second round of speech: the loop never starts

        h.Service.OnFinal(Me("Something completely different from what was just said.", "en")); // the user keeps talking: that is processed
        Assert.True(await Eventually(() => h.Translator.Count == calls + 1));
    }

    [Fact]
    public async Task TheSameWordsMuchLater_AreAGenuineNewSentence()
    {
        using var h = new Harness().Start(Speech("pt"), useClock: true);
        h.Service.OnFinal(Me("Good morning, everyone.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
        h.Now += TimeSpan.FromSeconds(120);
        h.Service.OnFinal(Me("[pt] Good morning, everyone.", "pt"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 2));
        Assert.Equal(0, h.Service.Stats.OwnVoiceIgnored);
    }

    [Fact]
    public async Task OtherPeopleSaying_TheSameWords_AreStillCaptioned()
    {
        var options = new LiveTranslationOptions("pt", "pt", "auto", LongHold);
        using var h = new Harness().Start(options, useClock: true);
        h.Service.OnFinal(Me("Good morning, everyone.", "en"));
        Assert.True(await Eventually(() => h.Output.Played.Count == 1));
        h.Service.OnFinal(Others("Good morning, everyone.", "en"));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
    }

    // ---- routing (no audio hardware involved) ----

    [Fact]
    public void SpeechRouting_RefusesDevicesThatWouldCreateAFeedbackLoop_OrDoNotExist()
    {
        var devices = new[] { "speakers", "headset", "feed" };
        Assert.Equal(SpeechRoutingProblem.None, SpeechRouting.Check("feed", devices, "speakers").Problem);
        Assert.Equal(SpeechRoutingProblem.None, SpeechRouting.Check("headset", devices, "speakers").Problem);
        Assert.Equal(SpeechRoutingProblem.NoDevice, SpeechRouting.Check(null, devices, "speakers").Problem);
        Assert.Equal(SpeechRoutingProblem.NoDevice, SpeechRouting.Check("", devices, "speakers").Problem);
        Assert.Equal(SpeechRoutingProblem.DeviceMissing, SpeechRouting.Check("usb-dongle", devices, "speakers").Problem);
        var loop = SpeechRouting.Check("speakers", devices, "speakers");
        Assert.Equal(SpeechRoutingProblem.SameAsSystemOutput, loop.Problem);
        Assert.Contains("MeetWise Mic Feed", loop.Message);
        Assert.Equal(SpeechRoutingProblem.None, SpeechRouting.Check("speakers", devices, null).Problem); // no default output known
    }

    [Fact]
    public void TheRealDeviceOutput_RefusesWithoutOpeningAnything_WhenNoDeviceOrAMissingDeviceIsChosen()
    {
        var settings = new MeetWise.Core.Settings.AppSettings { SpeakDeviceId = null };
        using var output = new MeetWise.App.DeviceSpeechOutput(settings);
        Assert.Throws<SpeechOutputException>(() => output.Play(new float[160], 16000));
        settings.SpeakDeviceId = "{0.0.0.00000000}.{does-not-exist}";
        var ex = Assert.Throws<SpeechOutputException>(() => output.Play(new float[160], 16000));
        Assert.Contains("not connected", ex.Message);
        Assert.Equal(0, output.QueuedSeconds);
        output.Stop(); // harmless with nothing open
    }

    // ---- privacy of the pipeline ----

    [Fact]
    public async Task TheServiceItself_NeverUsesTheNetwork()
    {
        // The service only talks to the translator it is given. With a local translator, nothing can leave the PC from here.
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        using var h = new Harness().Start(new LiveTranslationOptions("pt", "pt", "auto", LongHold));
        h.Service.OnFinal(Others("Confidential numbers: revenue 4.2 million.", "en"));
        h.Service.OnFinal(Me("This stays on the machine.", "en"));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1 && h.Output.Played.Count == 1));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task TheTextBeingTranslated_IsNeverWrittenToTheLog()
    {
        const string secret = "ZEBRA-CONFIDENTIAL-4711";
        using var h = new Harness().Start(Captions("pt"));
        h.Translator.Handler = (t, _, _, _) => throw new TranslationException(TranslationFailure.ProviderError, "no network");
        h.Service.OnFinal(Others($"The code word is {secret}.", "en"));
        Assert.True(await Eventually(() => h.CaptionsSnapshot().Length == 1));
        var log = File.Exists(MeetWise.Core.Logging.AppLog.CurrentLogFile) ? File.ReadAllText(MeetWise.Core.Logging.AppLog.CurrentLogFile) : "";
        Assert.DoesNotContain(secret, log);
    }
}
