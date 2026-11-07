using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MeetWise.Core.Logging;

namespace MeetWise.Intelligence;

/// <summary>One finished utterance from the transcript: <c>Others</c> (the system audio) or <c>Me</c> (the microphone).</summary>
public sealed record LiveUtterance(string Channel, string Text, string? Language, double Start, double End);

/// <summary>Text to speech for the user's translated sentences. Implemented over the Windows voices; tests use a fake.</summary>
public interface ISpeechSynthesizer
{
    /// <summary>Picks a voice for the language; false when none is installed.</summary>
    bool SelectLanguage(string language);
    Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, CancellationToken ct);
}

/// <summary>Where translated speech is played (the virtual microphone input, or any chosen output). Implementations create the device lazily and throw when it is gone.</summary>
public interface ISpeechOutput
{
    double QueuedSeconds { get; }
    void Play(float[] samples, int sampleRate);
    /// <summary>Stops immediately and discards whatever is still queued.</summary>
    void Stop();
}

public sealed class SpeechOutputException : Exception
{
    public SpeechOutputException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <param name="CaptionTarget">Translate what <c>Others</c> say into this language, as captions (null = off).</param>
/// <param name="SpeakTarget">Translate what <c>Me</c> says into this language and speak it ("same" = say it again with a neutral voice; null = off).</param>
/// <param name="SpokenLanguage">The language I speak ("auto" = as detected).</param>
/// <param name="Hold">An unfinished sentence is translated anyway after this much silence. Lower is faster, higher gives the model whole sentences.</param>
/// <param name="MaxChars">A monologue without sentence ends is cut at a clause boundary beyond this many characters.</param>
/// <param name="MaxPending">Sentences waiting per direction. When more arrive, the oldest waiting one is dropped (and reported): fresh beats stale.</param>
public sealed record LiveTranslationOptions(string? CaptionTarget, string? SpeakTarget, string SpokenLanguage = "auto", TimeSpan? Hold = null, int MaxChars = 220,
    int MaxPending = 4, TimeSpan? CaptionMaxAge = null, TimeSpan? SpeechMaxAge = null, double MaxQueuedSpeechSeconds = 10)
{
    public TimeSpan EffectiveHold => Hold ?? TimeSpan.FromMilliseconds(1200);
    public TimeSpan EffectiveCaptionMaxAge => CaptionMaxAge ?? TimeSpan.FromSeconds(20);
    public TimeSpan EffectiveSpeechMaxAge => SpeechMaxAge ?? TimeSpan.FromSeconds(8);
}

/// <param name="Translated">Null when the translation failed; <paramref name="Error"/> then says why and the original is still shown.</param>
/// <param name="Doubtful">The translation was accepted but something looked off (a number that changed, a lost question mark).</param>
public sealed record TranslatedCaption(long Seq, string Channel, string Original, string? Translated, string? SourceLanguage, string TargetLanguage, TimeSpan Latency, string? Error = null, bool Doubtful = false);

public enum TranslationStatusKind { Ok, Degraded, Skipped, VoiceMissing, OutputUnavailable }

/// <summary>A change of state worth telling the user about. Sent once per change, never once per sentence.</summary>
public sealed record TranslationStatus(TranslationStatusKind Kind, string Message);

public sealed class LiveTranslationStats
{
    private readonly List<double> _caption = new(), _speech = new();
    private readonly object _gate = new();
    public int Received, Translated, Failed, Dropped, SameLanguage, OwnVoiceIgnored, Spoken;

    internal void AddCaptionLatency(TimeSpan t) { lock (_gate) { _caption.Add(t.TotalMilliseconds); if (_caption.Count > 200) _caption.RemoveAt(0); } }
    internal void AddSpeechLatency(TimeSpan t) { lock (_gate) { _speech.Add(t.TotalMilliseconds); if (_speech.Count > 200) _speech.RemoveAt(0); } }
    public double CaptionLatencyP50Ms => Percentile(_caption, 0.5);
    public double CaptionLatencyP95Ms => Percentile(_caption, 0.95);
    public double SpeechLatencyP50Ms => Percentile(_speech, 0.5);
    public double SpeechLatencyP95Ms => Percentile(_speech, 0.95);

    private double Percentile(List<double> l, double p)
    {
        lock (_gate)
        {
            if (l.Count == 0) return 0;
            var s = l.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(s.Count * p))];
        }
    }
}

/// <summary>
/// Live translation of a meeting, kept off every thread that matters. <see cref="OnFinal"/> only queues work and returns at once, so audio capture,
/// transcription and the UI can never wait for a model:
/// <list type="bullet">
/// <item><b>Segmentation.</b> Fragments from the recognizer are joined into sentences (several sentences in one utterance are split; a sentence
/// that has not ended is translated after <see cref="LiveTranslationOptions.Hold"/>; a very long run is cut at a clause boundary).</item>
/// <item><b>Language.</b> The detected language is sticky: one short or noisy utterance does not flip it.</item>
/// <item><b>Order and load.</b> One worker per direction keeps output in order. At most <see cref="LiveTranslationOptions.MaxPending"/> sentences wait;
/// the oldest is dropped (and reported) when more arrive, and anything that waited too long is skipped. Nothing is dropped silently.</item>
/// <item><b>Failures</b> (timeout, provider down, rate limit, bad answer, no voice, no output device) become one status message per change of state,
/// and a caption that could not be translated still shows the original with the reason.</item>
/// <item><b>Own voice.</b> Speech the app plays is remembered, so if the microphone picks it up again it is not translated a second time.</item>
/// </list>
/// Transcript text is never written to a log.
/// </summary>
public sealed class LiveTranslationService : IDisposable
{
    private sealed record Job(long Seq, string Channel, string Text, string? Source, DateTime Received, int Generation);

    private readonly ITranslator _translator;
    private readonly IDetailedTranslator? _detailed;
    private readonly LiveTranslationOptions _o;
    private readonly ISpeechSynthesizer? _tts;
    private readonly ISpeechOutput? _output;
    private readonly Func<DateTime> _now;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Job> _captionQueue, _speechQueue;
    private readonly SentenceAssembler _othersBuffer = new(), _meBuffer = new();
    private readonly LanguageTracker _othersLang = new(), _meLang = new();
    private readonly SelfSpeechGuard _guard = new();
    private readonly object _gate = new();
    private readonly Timer _holdOthers, _holdMe;
    private readonly Task _captionWorker, _speechWorker;
    private CancellationTokenSource _speechCts = new();
    private int _generation;
    private long _seq;
    private string? _captionState = "ok", _speechTranslateState = "ok", _speechOutputState = "ok";
    private bool _disposed;

    public event Action<TranslatedCaption>? Caption;
    public event Action<TranslationStatus>? Status;
    public LiveTranslationStats Stats { get; } = new();

    public LiveTranslationService(ITranslator translator, LiveTranslationOptions options, ISpeechSynthesizer? tts = null, ISpeechOutput? output = null, Func<DateTime>? now = null)
    {
        _translator = translator;
        _detailed = translator as IDetailedTranslator;
        _o = options;
        _tts = tts;
        _output = output;
        _now = now ?? (() => DateTime.UtcNow);
        BoundedChannelOptions Bounded() => new(Math.Max(1, options.MaxPending)) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true };
        _captionQueue = Channel.CreateBounded<Job>(Bounded(), job => OnDropped(job));
        _speechQueue = Channel.CreateBounded<Job>(Bounded(), job => OnDropped(job));
        _holdOthers = new Timer(_ => FlushStale("Others"), null, Timeout.Infinite, Timeout.Infinite);
        _holdMe = new Timer(_ => FlushStale("Me"), null, Timeout.Infinite, Timeout.Infinite);
        _captionWorker = Task.Run(() => RunAsync(_captionQueue, CaptionAsync));
        _speechWorker = Task.Run(() => RunAsync(_speechQueue, SpeakAsync));
    }

    public bool IsEnabled => _o.CaptionTarget is not null || _o.SpeakTarget is not null;

    /// <summary>Sentences waiting to be translated, per direction (tests and diagnostics).</summary>
    public int Pending => _captionQueue.Reader.Count + _speechQueue.Reader.Count;

    // ---- input: never blocks ----

    public void OnFinal(LiveUtterance u)
    {
        if (_disposed || string.IsNullOrWhiteSpace(u.Text)) return;
        bool others = u.Channel == "Others", me = u.Channel == "Me";
        if (others && _o.CaptionTarget is null) return;
        if (me && _o.SpeakTarget is null) return;
        if (!others && !me) return;
        Interlocked.Increment(ref Stats.Received);

        if (me && _guard.IsOwnVoice(u.Text, _now()))
        {
            Interlocked.Increment(ref Stats.OwnVoiceIgnored); // the microphone heard what this app just said
            return;
        }

        lock (_gate)
        {
            var tracker = others ? _othersLang : _meLang;
            var buffer = others ? _othersBuffer : _meBuffer;
            int words = u.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var lang = tracker.Observe(u.Language, words);
            foreach (var unit in buffer.Add(u.Text, lang, _now(), _o.MaxChars)) Enqueue(u.Channel, unit);
            (others ? _holdOthers : _holdMe).Change(buffer.HasText ? _o.EffectiveHold : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the translated voice now: cancels synthesis in progress, empties the playback queue and forgets sentences still waiting to be spoken.</summary>
    public void StopSpeaking()
    {
        Interlocked.Increment(ref _generation);
        var old = Interlocked.Exchange(ref _speechCts, new CancellationTokenSource());
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        try { _output?.Stop(); } catch (Exception ex) { AppLog.Warn("Stopping the translated voice failed: " + SecretRedaction.Redact(ex.Message)); }
        while (_speechQueue.Reader.TryRead(out _)) Interlocked.Increment(ref Stats.Dropped);
    }

    private void FlushStale(string channel)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var buffer = channel == "Others" ? _othersBuffer : _meBuffer;
            if (buffer.Flush() is { } unit) Enqueue(channel, unit);
        }
    }

    private void Enqueue(string channel, SentenceUnit unit)
    {
        var job = new Job(Interlocked.Increment(ref _seq), channel, unit.Text, unit.Language, _now(), Volatile.Read(ref _generation));
        (channel == "Others" ? _captionQueue : _speechQueue).Writer.TryWrite(job);
    }

    private void OnDropped(Job job)
    {
        Interlocked.Increment(ref Stats.Dropped);
        Raise(new TranslationStatus(TranslationStatusKind.Skipped, "Translation is behind: an older sentence was skipped to keep up."));
    }

    // ---- workers ----

    private async Task RunAsync(Channel<Job> queue, Func<Job, Task> handle)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var maxAge = job.Channel == "Others" ? _o.EffectiveCaptionMaxAge : _o.EffectiveSpeechMaxAge;
                if (_now() - job.Received > maxAge)
                {
                    Interlocked.Increment(ref Stats.Dropped);
                    Raise(new TranslationStatus(TranslationStatusKind.Skipped, "Translation is behind: a sentence that waited too long was skipped."));
                    continue;
                }
                try { await handle(job).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { /* StopSpeaking cancelled this one */ }
                catch (Exception ex) { AppLog.Warn("Live translation step failed: " + SecretRedaction.Redact(ex.GetType().Name)); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task CaptionAsync(Job job)
    {
        var target = _o.CaptionTarget!;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var res = await TranslateAsync(job.Text, target, job.Source, _cts.Token).ConfigureAwait(false);
            if (res is null) { Interlocked.Increment(ref Stats.SameLanguage); return; } // already in the target language: nothing to show
            var latency = _now() - job.Received;
            Stats.AddCaptionLatency(latency);
            Interlocked.Increment(ref Stats.Translated);
            SetState(ref _captionState, "ok", new TranslationStatus(TranslationStatusKind.Ok, "Live translation is working again."));
            Caption?.Invoke(new TranslatedCaption(job.Seq, "Others", job.Text, res.Text, job.Source, target, latency, null, res.Warnings.Count > 0));
        }
        catch (TranslationException ex)
        {
            Interlocked.Increment(ref Stats.Failed);
            SetState(ref _captionState, ex.Kind.ToString(), new TranslationStatus(TranslationStatusKind.Degraded, "Live translation unavailable: " + ex.Message));
            // never drop the words silently: show the original with the reason
            Caption?.Invoke(new TranslatedCaption(job.Seq, "Others", job.Text, null, job.Source, target, _now() - job.Received, ex.Message));
        }
    }

    private async Task SpeakAsync(Job job)
    {
        if (job.Generation != Volatile.Read(ref _generation)) { Interlocked.Increment(ref Stats.Dropped); return; }
        var target = _o.SpeakTarget!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _speechCts.Token);
        var ct = linked.Token;
        string spoken;
        string language;
        if (target == "same")
        {
            spoken = job.Text;
            language = LlmTranslator.Normalize(_o.SpokenLanguage) ?? LlmTranslator.Normalize(job.Source) ?? "en";
        }
        else
        {
            try
            {
                var res = await TranslateAsync(job.Text, target, LlmTranslator.Normalize(_o.SpokenLanguage) ?? job.Source, ct).ConfigureAwait(false);
                spoken = res?.Text ?? job.Text; // already in the target language: say it as it is
                SetState(ref _speechTranslateState, "ok", new TranslationStatus(TranslationStatusKind.Ok, "Spoken translation is working again."));
            }
            catch (TranslationException ex)
            {
                Interlocked.Increment(ref Stats.Failed);
                SetState(ref _speechTranslateState, ex.Kind.ToString(), new TranslationStatus(TranslationStatusKind.Degraded, "A sentence of yours could not be translated and was not spoken: " + ex.Message));
                return;
            }
            language = LlmTranslator.Normalize(target) ?? target;
        }
        if (_tts is null || _output is null) return;
        if (!_tts.SelectLanguage(language))
        {
            SetState(ref _speechOutputState, "voice:" + language, new TranslationStatus(TranslationStatusKind.VoiceMissing,
                $"No Windows voice is installed for '{language}'. Add it in Windows Settings, Time and language, Speech."));
            return;
        }
        try
        {
            var (samples, rate) = await _tts.SynthesizeAsync(spoken, ct).ConfigureAwait(false);
            if (job.Generation != Volatile.Read(ref _generation)) return; // stopped while synthesizing
            // Stale translated speech is worse than none: skip it when the output is already far behind.
            if (_output.QueuedSeconds > _o.MaxQueuedSpeechSeconds)
            {
                Interlocked.Increment(ref Stats.Dropped);
                Raise(new TranslationStatus(TranslationStatusKind.Skipped, "The translated voice is far behind; a sentence was skipped."));
                return;
            }
            var ahead = _output.QueuedSeconds;
            _output.Play(samples, rate);
            _guard.Spoke(spoken, _now().AddSeconds(ahead), TimeSpan.FromSeconds(samples.Length / (double)rate));
            Stats.AddSpeechLatency(_now() - job.Received);
            Interlocked.Increment(ref Stats.Spoken);
            SetState(ref _speechOutputState, "ok", new TranslationStatus(TranslationStatusKind.Ok, "The translated voice is working again."));
        }
        catch (OperationCanceledException) { throw; }
        catch (SpeechOutputException ex)
        {
            SetState(ref _speechOutputState, "output", new TranslationStatus(TranslationStatusKind.OutputUnavailable, "The output device for the translated voice is not available: " + ex.Message));
        }
        catch (Exception ex)
        {
            SetState(ref _speechOutputState, "tts", new TranslationStatus(TranslationStatusKind.Degraded, "The Windows voice failed: " + SecretRedaction.Redact(ex.Message)));
        }
    }

    /// <summary>Returns null when the text is already in the target language (no model call, nothing to show).</summary>
    private async Task<TranslationResult?> TranslateAsync(string text, string target, string? source, CancellationToken ct)
    {
        var src = LlmTranslator.Normalize(source);
        if (src is not null && src == LlmTranslator.Normalize(target)) return null;
        if (_detailed is not null) return await _detailed.TranslateDetailedAsync(text, target, src, ct).ConfigureAwait(false);
        return new TranslationResult(await _translator.TranslateAsync(text, target, src, ct).ConfigureAwait(false), Array.Empty<string>());
    }

    private void SetState(ref string? state, string next, TranslationStatus message)
    {
        if (state == next) return;
        state = next;
        Raise(message);
    }

    private void Raise(TranslationStatus s)
    {
        try { Status?.Invoke(s); } catch (Exception ex) { AppLog.Warn("Translation status handler failed: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _holdOthers.Dispose();
        _holdMe.Dispose();
        _cts.Cancel();
        try { _speechCts.Cancel(); } catch (ObjectDisposedException) { }
        _captionQueue.Writer.TryComplete();
        _speechQueue.Writer.TryComplete();
        try { _output?.Stop(); } catch (Exception) { }
        try { Task.WaitAll(new[] { _captionWorker, _speechWorker }, TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}

/// <summary>A translator that can also report doubts about its answer.</summary>
public interface IDetailedTranslator : ITranslator
{
    Task<TranslationResult> TranslateDetailedAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default);
}

internal sealed record SentenceUnit(string Text, string? Language);

/// <summary>Joins recognizer fragments and cuts them into sentences, so translation sees whole thoughts and not half a clause.</summary>
internal sealed partial class SentenceAssembler
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "mr", "mrs", "ms", "dr", "prof", "sr", "sra", "jr", "vs", "etc", "e.g", "i.e", "no", "st", "inc", "ltd", "approx", "dept", "est" };

    private readonly StringBuilder _buf = new();
    private string? _language;

    public bool HasText => _buf.Length > 0;

    public IReadOnlyList<SentenceUnit> Add(string text, string? language, DateTime now, int maxChars)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length == 0) return Array.Empty<SentenceUnit>();
        if (_buf.Length > 0) _buf.Append(' ');
        _buf.Append(text);
        _language = language ?? _language;
        var result = new List<SentenceUnit>();
        while (TakeSentence(maxChars) is { } s) result.Add(new SentenceUnit(s, _language));
        return result;
    }

    /// <summary>Whatever is left, as one unit (the hold timer fired, or the speaker changed).</summary>
    public SentenceUnit? Flush()
    {
        var text = _buf.ToString().Trim();
        _buf.Clear();
        return text.Length == 0 ? null : new SentenceUnit(text, _language);
    }

    private string? TakeSentence(int maxChars)
    {
        var s = _buf.ToString();
        int cut = FindSentenceEnd(s);
        if (cut < 0 && s.Length > maxChars) cut = FindClauseBreak(s, maxChars);
        if (cut < 0) return null;
        var sentence = s[..cut].Trim();
        _buf.Clear();
        _buf.Append(s[cut..].TrimStart());
        return sentence.Length == 0 ? TakeSentence(maxChars) : sentence;
    }

    private static int FindSentenceEnd(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is not ('.' or '?' or '!' or '…' or '。' or '？' or '！' or '؟')) continue;
            int end = i + 1;
            while (end < s.Length && s[end] is '.' or '?' or '!' or '…' or '"' or '\'' or '”' or '»' or ')') end++;
            bool atEnd = end >= s.Length;
            bool spaceAfter = !atEnd && s[end] == ' ';
            bool cjk = c is '。' or '？' or '！';
            if (!(atEnd || spaceAfter || cjk)) continue; // "3.5", "e.g.x"
            if (c == '.')
            {
                // abbreviation: "Dr. Silva", "e.g. this"
                int start = i - 1;
                while (start >= 0 && (char.IsLetter(s[start]) || s[start] == '.')) start--;
                var word = s[(start + 1)..i];
                if (Abbreviations.Contains(word) && !atEnd) continue;
                if (i + 1 < s.Length && char.IsDigit(s[i + 1])) continue;
            }
            return end; // when the sentence ends exactly at the end of the buffer it is complete too
        }
        return -1;
    }

    private static int FindClauseBreak(string s, int maxChars)
    {
        int limit = Math.Min(s.Length - 1, maxChars);
        for (int i = limit; i > maxChars / 2; i--)
            if (s[i] is ',' or ';' or ':' or '—' or '-') return i + 1;
        int space = s.LastIndexOf(' ', limit);
        return space > maxChars / 2 ? space + 1 : -1;
    }
}

/// <summary>Keeps the language of a speaker steady: a short or noisy utterance must not flip it back and forth.</summary>
internal sealed class LanguageTracker
{
    private string? _current, _candidate;
    private int _hits;

    public string? Observe(string? detected, int words)
    {
        var lang = LlmTranslator.Normalize(detected);
        if (lang is null) return _current;                 // unknown or unsupported: keep what we had
        if (_current is null) { _current = lang; return _current; }
        if (lang == _current) { _candidate = null; _hits = 0; return _current; }
        if (words < 3) return _current;                    // too short to say a language changed
        if (words >= 6) { Switch(lang); return _current; } // a full sentence in another language is a real switch
        if (_candidate == lang) _hits++; else { _candidate = lang; _hits = 1; }
        if (_hits >= 2) Switch(lang);
        return _current;
    }

    private void Switch(string lang) { _current = lang; _candidate = null; _hits = 0; }
}

/// <summary>
/// Remembers what the translated voice said. Translated speech goes to the virtual microphone, which the recorder may also be listening to; without this the
/// app would hear its own voice, translate it again and say it again, forever.
/// </summary>
internal sealed class SelfSpeechGuard
{
    private sealed record Spoken(HashSet<string> Words, DateTime From, DateTime Until);
    private readonly List<Spoken> _recent = new();
    private readonly object _gate = new();

    public void Spoke(string text, DateTime playStart, TimeSpan duration)
    {
        var words = WordsOf(text);
        if (words.Count == 0) return;
        lock (_gate)
        {
            _recent.Add(new Spoken(words, playStart, playStart + duration));
            _recent.RemoveAll(s => s.Until < playStart - TimeSpan.FromSeconds(60));
        }
    }

    public bool IsOwnVoice(string heard, DateTime now)
    {
        var words = WordsOf(heard);
        if (words.Count == 0) return false;
        lock (_gate)
        {
            foreach (var s in _recent)
            {
                double overlap = words.Count(w => s.Words.Contains(w)) / (double)Math.Max(words.Count, 1);
                bool playing = now >= s.From - TimeSpan.FromSeconds(1) && now <= s.Until + TimeSpan.FromSeconds(4);
                if ((playing && overlap >= 0.6) || (now - s.Until < TimeSpan.FromSeconds(30) && overlap >= 0.85 && words.Count >= 3)) return true;
            }
        }
        return false;
    }

    private static HashSet<string> WordsOf(string text)
        => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToHashSet();
}
