using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeetWise.Intelligence;
using MeetWise.Storage;

namespace MeetWise.Tests;

public class CoachTests
{
    private const string Bad = """{"ok":false,"corrected":"I went to the office yesterday and met my manager.","issues":[{"type":"grammar","original":"I goes","suggestion":"I went","why":"Use the past tense for yesterday."},{"type":"grammar","original":"meet","suggestion":"met","why":"Past tense of meet."}]}""";

    [Fact]
    public void Parse_ReadsIssues_AndCorrectedSentence()
    {
        var fb = SpeechCoach.Parse("Here you go: " + Bad, "I goes to the office yesterday and meet my manager.", 140, 12.5);
        Assert.False(fb.Ok);
        Assert.Equal(2, fb.Issues.Count);
        Assert.Equal("I went", fb.Issues[0].Suggestion);
        Assert.Equal("I went to the office yesterday and met my manager.", fb.Corrected);
        Assert.Equal(140, fb.WordsPerMinute);
        Assert.Equal(12.5, fb.StartSec);
    }

    [Fact]
    public void Parse_IgnoresNoise_UnchangedCorrections_AndGarbage()
    {
        // a "correction" that only changes case or punctuation is noise from ASR, not a mistake
        var noise = SpeechCoach.Parse("""{"ok":false,"corrected":"We should meet.","issues":[{"type":"grammar","original":"we should meet","suggestion":"We should meet.","why":"Capitalise."}]}""", "we should meet");
        Assert.True(noise.Ok);
        Assert.Empty(noise.Issues);
        Assert.Equal("we should meet", noise.Corrected);

        foreach (var junk in new[] { "I cannot help with that.", "{ broken json", "", "[1,2,3]" })
        {
            var fb = SpeechCoach.Parse(junk, "some sentence here");
            Assert.True(fb.Ok);
            Assert.Equal("some sentence here", fb.Corrected);
        }
    }

    [Fact]
    public async Task Review_SendsRulesThatProtectAgainstAsrFalsePositives()
    {
        var llm = new FakeLlm { Reply = _ => Bad };
        var fb = await new SpeechCoach(llm).ReviewAsync("I goes to the office yesterday and meet my manager.");
        Assert.Contains("IGNORE capitalisation", llm.Prompts[0].System);
        Assert.Contains("recognition error", llm.Prompts[0].System);
        Assert.Contains("ok=true", llm.Prompts[0].System);
        Assert.Equal("I goes to the office yesterday and meet my manager.", llm.Prompts[0].User);
        Assert.False(fb.Ok);
    }

    [Fact]
    public void WordDiff_MarksRemovedAndAddedWords_IgnoringCaseAndPunctuation()
    {
        var diff = WordDiff.Compute("I goes to the office yesterday.", "I went to the office yesterday.");
        Assert.Equal("goes", Assert.Single(diff, d => d.Kind == DiffKind.Removed).Text);
        Assert.Equal("went", Assert.Single(diff, d => d.Kind == DiffKind.Added).Text);
        Assert.Equal(5, diff.Count(d => d.Kind == DiffKind.Same));

        var same = WordDiff.Compute("we should meet", "We should meet.");
        Assert.All(same, d => Assert.Equal(DiffKind.Same, d.Kind));
        Assert.Contains(WordDiff.Compute("She don't know", "She doesn't know"), d => d is { Kind: DiffKind.Removed, Text: "don't" });
        Assert.Empty(WordDiff.Compute("", ""));
    }

    [Fact]
    public void Wpm_NeedsEnoughWordsAndTime()
    {
        Assert.Equal(120, SpeechCoach.WordsPerMinute("one two three four five six", 0, 3)!.Value, 3);
        Assert.Null(SpeechCoach.WordsPerMinute("too short", 0, 3));
        Assert.Null(SpeechCoach.WordsPerMinute("one two three four five six", 0, 0.5));
    }

    private sealed class SlowLlm : ILlmProvider
    {
        public string Name => "slow";
        public int ContextTokens => 4096;
        public readonly List<string> Seen = new();
        public int DelayMs = 150;
        public bool ThrowOnce;

        public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
        {
            await Task.Delay(DelayMs, ct);
            lock (Seen) Seen.Add(messages[^1].Content);
            if (ThrowOnce) { ThrowOnce = false; throw new InvalidOperationException("model hiccup"); }
            return """{"ok":true,"corrected":"x","issues":[]}""";
        }
    }

    [Fact]
    public async Task Session_SkipsShortSentences_PunctuatesFirst_AndKeepsWorking_AfterAnError()
    {
        var llm = new SlowLlm { DelayMs = 20, ThrowOnce = true };
        var got = new List<CoachFeedback>();
        using (var session = new CoachSession(new SpeechCoach(llm), t => char.ToUpper(t[0]) + t[1..].ToLowerInvariant() + "."))
        {
            session.Feedback += f => { lock (got) got.Add(f); };
            session.Submit("YES OK", 0, 1);                                    // too short: never reviewed
            session.Submit("THIS FIRST SENTENCE TRIGGERS A HICCUP", 1, 4);     // model throws: session must survive
            await Task.Delay(300);
            session.Submit("THIS SECOND SENTENCE IS REVIEWED NORMALLY", 5, 8);
            await Task.Delay(400);
        }
        Assert.DoesNotContain(llm.Seen, s => s.StartsWith("Yes"));
        Assert.Contains("This second sentence is reviewed normally.", llm.Seen);
        Assert.Single(got);
        Assert.Equal(5, got[0].StartSec);
    }

    [Fact]
    public async Task Session_DropsStaleSentences_WhenTheModelIsSlowerThanTheSpeaker()
    {
        var llm = new SlowLlm { DelayMs = 250 };
        using (var session = new CoachSession(new SpeechCoach(llm), maxPending: 2))
        {
            for (int i = 0; i < 10; i++) session.Submit($"SENTENCE NUMBER {i} IS SPOKEN QUICKLY HERE", i * 3, i * 3 + 2);
            await Task.Delay(1500);
        }
        lock (llm.Seen)
        {
            Assert.True(llm.Seen.Count < 10, $"reviewed {llm.Seen.Count} of 10: stale ones must be dropped");
            Assert.Contains(llm.Seen, s => s.Contains("NUMBER 9"));   // the freshest sentence is always reviewed
        }
    }

    [Fact]
    public void Report_SummarisesRecurringMistakes_AndPace()
    {
        CoachFeedback F(bool ok, double? wpm, params (string o, string s)[] issues) =>
            new("x", "y", ok, issues.Select(i => new CoachIssue("grammar", i.o, i.s, "")).ToList(), wpm, 0);
        var report = CoachReportBuilder.Build(new[]
        {
            F(false, 130, ("don't", "doesn't")), F(false, 150, ("Don't", "doesn't"), ("meet", "met")), F(true, 140), F(true, null)
        });
        Assert.Equal(4, report.Sentences);
        Assert.Equal(2, report.WithIssues);
        Assert.Equal(140, report.AverageWpm!.Value, 3);
        Assert.Equal(("don't -> doesn't", 2), report.Recurring[0]);
    }

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), "mw-coach-" + Guid.NewGuid().ToString("N"), "t.db");

    [Fact]
    public void Database_StoresCoaching_PerMeeting()
    {
        var db = new MeetingDatabase(NewDbPath());
        var m = db.AddMeeting("Standup", DateTime.UtcNow);
        var other = db.AddMeeting("Other", DateTime.UtcNow);
        db.AddCoaching(m, 12, "I goes", "I go", false, JsonSerializer.Serialize(new[] { new { original = "goes", suggestion = "go" } }), 128);
        db.AddCoaching(m, 3, "Fine sentence here", "Fine sentence here", true, "[]", null);
        db.AddCoaching(other, 1, "x", "x", true, "[]", null);

        var entries = db.GetCoaching(m);
        Assert.Equal(new[] { 3.0, 12.0 }, entries.Select(e => e.StartSec));
        Assert.Equal(128, entries[1].Wpm);
        Assert.Null(entries[0].Wpm);
        Assert.False(entries[1].Ok);
    }

    [Fact]
    public void Database_UpgradesAnExistingVersion1File_WithoutLosingData()
    {
        var path = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // build a database exactly as the first release created it (schema version 1)
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meetings(id INTEGER PRIMARY KEY, title TEXT NOT NULL, started_utc TEXT NOT NULL, series_id TEXT, folder TEXT, audio_path TEXT, summary TEXT);
                CREATE TABLE segments(id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, speaker TEXT NOT NULL, start_sec REAL NOT NULL, end_sec REAL NOT NULL, text TEXT NOT NULL, language TEXT);
                CREATE INDEX ix_segments_meeting ON segments(meeting_id, start_sec);
                CREATE VIRTUAL TABLE segments_fts USING fts5(text, content='segments', content_rowid='id');
                CREATE TRIGGER segments_ai AFTER INSERT ON segments BEGIN INSERT INTO segments_fts(rowid, text) VALUES (new.id, new.text); END;
                CREATE TRIGGER segments_ad AFTER DELETE ON segments BEGIN INSERT INTO segments_fts(segments_fts, rowid, text) VALUES('delete', old.id, old.text); END;
                CREATE TABLE action_items(id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, text TEXT NOT NULL, owner TEXT, due_utc TEXT, done INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE tags(meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY(meeting_id, tag));
                INSERT INTO meetings(id, title, started_utc) VALUES(1, 'Old meeting', '2026-01-01T10:00:00.0000000Z');
                INSERT INTO segments(meeting_id, speaker, start_sec, end_sec, text) VALUES(1, 'Me', 0, 2, 'legacy words survive');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new MeetingDatabase(path);
        Assert.Equal("Old meeting", db.GetMeeting(1)!.Title);
        Assert.Single(db.Search("legacy"));
        db.AddCoaching(1, 1, "a b c d", "a b c d", true, "[]", null);
        Assert.Single(db.GetCoaching(1));
        _ = new MeetingDatabase(path); // opening again must be a no-op
        Assert.Single(db.GetCoaching(1));
    }
}
