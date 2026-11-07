using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MeetWise.Intelligence;

public sealed record CoachIssue(string Type, string Original, string Suggestion, string Why);

public sealed record CoachFeedback(string Original, string Corrected, bool Ok, IReadOnlyList<CoachIssue> Issues, double? WordsPerMinute, double StartSec);

public enum DiffKind { Same, Removed, Added }

public sealed record DiffToken(string Text, DiffKind Kind);

/// <summary>Word-level difference between what was said and the corrected sentence, for striking through mistakes on screen.</summary>
public static class WordDiff
{
    private static string Norm(string w) => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static IReadOnlyList<DiffToken> Compute(string original, string corrected)
    {
        var a = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = corrected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // longest common subsequence on normalised words, so "Meeting," and "meeting" count as the same
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = Norm(a[i]) == Norm(b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffToken>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (Norm(a[x]) == Norm(b[y])) { result.Add(new DiffToken(b[y], DiffKind.Same)); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new DiffToken(a[x++], DiffKind.Removed));
            else result.Add(new DiffToken(b[y++], DiffKind.Added));
        }
        while (x < a.Length) result.Add(new DiffToken(a[x++], DiffKind.Removed));
        while (y < b.Length) result.Add(new DiffToken(b[y++], DiffKind.Added));
        return result;
    }
}

/// <summary>Reviews one spoken English sentence and reports real grammar and word-choice mistakes, privately.</summary>
public sealed class SpeechCoach
{
    public const int MinWords = 4;

    private const string Instructions = """
        You are a friendly English speaking coach for a non-native speaker in business meetings.
        The sentence comes from automatic speech recognition: IGNORE capitalisation, punctuation, spelling, and any word that looks like a recognition error (a similar-sounding word that does not fit).
        Report ONLY real mistakes: grammar (tense, agreement, articles, prepositions, word order) and clearly unnatural word choice.
        Do not rewrite style, do not make the sentence fancier, and do not flag things that are acceptable in spoken English (contractions, starting with "and" or "so", fillers).
        If the sentence is acceptable, answer ok=true with no issues.
        Reply with ONLY JSON: {"ok": boolean, "corrected": string, "issues": [{"type": "grammar"|"word-choice"|"clarity", "original": string, "suggestion": string, "why": string (max 12 words)}]}.

        Examples:
        "I goes to the office yesterday and meet my manager." -> {"ok":false,"corrected":"I went to the office yesterday and met my manager.","issues":[{"type":"grammar","original":"I goes","suggestion":"I went","why":"Use the past tense for yesterday."},{"type":"grammar","original":"meet","suggestion":"met","why":"Past tense of meet."}]}
        "We should schedule the meeting for Thursday." -> {"ok":true,"corrected":"We should schedule the meeting for Thursday.","issues":[]}
        "She don't know the answer." -> {"ok":false,"corrected":"She doesn't know the answer.","issues":[{"type":"grammar","original":"don't","suggestion":"doesn't","why":"Third person singular needs does."}]}
        """;

    private readonly ILlmProvider _llm;

    public SpeechCoach(ILlmProvider llm) => _llm = llm;

    public async Task<CoachFeedback> ReviewAsync(string sentence, double? wordsPerMinute = null, double startSec = 0, CancellationToken ct = default)
    {
        var reply = await _llm.CompleteAsync(Instructions, new[] { new ChatMessage("user", sentence) }, 400, ct);
        return Parse(reply, sentence, wordsPerMinute, startSec);
    }

    /// <summary>Tolerant parse: a model that answers badly yields "no issues" rather than inventing corrections.</summary>
    public static CoachFeedback Parse(string reply, string original, double? wpm = null, double startSec = 0)
    {
        try
        {
            var json = MeetingIntelligence.ExtractJsonObject(reply);
            if (json is null) return new CoachFeedback(original, original, true, Array.Empty<CoachIssue>(), wpm, startSec);
            var root = JsonNode.Parse(json)!.AsObject();
            var issues = new List<CoachIssue>();
            if (root["issues"] is JsonArray arr)
                foreach (var i in arr)
                {
                    var o = i?["original"]?.ToString();
                    var s = i?["suggestion"]?.ToString();
                    if (string.IsNullOrWhiteSpace(o) || string.IsNullOrWhiteSpace(s) || Same(o!, s!)) continue; // an unchanged "correction" is noise
                    issues.Add(new CoachIssue(i?["type"]?.ToString() ?? "grammar", o!, s!, i?["why"]?.ToString() ?? ""));
                }
            var corrected = root["corrected"]?.ToString();
            bool ok = issues.Count == 0;
            if (ok || string.IsNullOrWhiteSpace(corrected)) corrected = ok ? original : corrected ?? original;
            return new CoachFeedback(original, corrected!, ok, issues, wpm, startSec);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return new CoachFeedback(original, original, true, Array.Empty<CoachIssue>(), wpm, startSec);
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(new string(a.Where(char.IsLetterOrDigit).ToArray()), new string(b.Where(char.IsLetterOrDigit).ToArray()), StringComparison.OrdinalIgnoreCase);

    public static double? WordsPerMinute(string text, double startSec, double endSec)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var seconds = endSec - startSec;
        return seconds >= 1.5 && words >= MinWords ? words / seconds * 60 : null;
    }
}

/// <summary>
/// Feeds spoken sentences to the coach without ever falling behind the conversation: very short sentences are skipped and, if the
/// model is slower than the speaker, the oldest waiting sentences are dropped so feedback stays about what was just said.
/// </summary>
public sealed class CoachSession : IDisposable
{
    private readonly SpeechCoach _coach;
    private readonly Func<string, string> _punctuate;
    private readonly Channel<(string Text, double Start, double End)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public event Action<CoachFeedback>? Feedback;

    public CoachSession(SpeechCoach coach, Func<string, string>? punctuate = null, int maxPending = 3)
    {
        _coach = coach;
        _punctuate = punctuate ?? (t => t);
        _queue = Channel.CreateBounded<(string, double, double)>(new BoundedChannelOptions(maxPending) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _worker = Task.Run(RunAsync);
    }

    public void Submit(string rawText, double startSec, double endSec)
    {
        if (rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < SpeechCoach.MinWords) return;
        _queue.Writer.TryWrite((rawText, startSec, endSec));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var (text, start, end) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    var sentence = _punctuate(text);
                    var fb = await _coach.ReviewAsync(sentence, SpeechCoach.WordsPerMinute(text, start, end), start, _cts.Token);
                    Feedback?.Invoke(fb);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { /* a failed review must never stop the session */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}

public sealed record CoachReport(int Sentences, int WithIssues, double? AverageWpm, IReadOnlyList<(string Pattern, int Count)> Recurring);

public static class CoachReportBuilder
{
    public static CoachReport Build(IEnumerable<CoachFeedback> feedback)
    {
        var all = feedback.ToList();
        var recurring = all.SelectMany(f => f.Issues)
            .GroupBy(i => $"{i.Original.Trim().ToLowerInvariant()} -> {i.Suggestion.Trim().ToLowerInvariant()}")
            .Select(g => (Pattern: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Pattern).Take(8).ToList();
        var wpm = all.Where(f => f.WordsPerMinute is not null).Select(f => f.WordsPerMinute!.Value).ToList();
        return new CoachReport(all.Count, all.Count(f => !f.Ok), wpm.Count > 0 ? wpm.Average() : null, recurring);
    }
}
