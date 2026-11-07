using Microsoft.Data.Sqlite;
using MeetWise.Storage;

namespace MeetWise.Tests;

public class StudioStorageTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "mw-st-" + Guid.NewGuid().ToString("N"), "t.db");

    [Fact]
    public void Upgrade_FromTheCurrentReleasesVersion2File_KeepsEverything_AndAddsStudioColumns()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
                CREATE TABLE coaching(id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, start_sec REAL NOT NULL, original TEXT NOT NULL, corrected TEXT NOT NULL, ok INTEGER NOT NULL, issues_json TEXT NOT NULL, wpm REAL);
                INSERT INTO meetings(id, title, started_utc, audio_path, summary) VALUES(1, 'Old call', '2026-05-01T09:00:00.0000000Z', 'C:\old.mic.wav', '{"summary":"kept"}');
                INSERT INTO segments(meeting_id, speaker, start_sec, end_sec, text) VALUES(1, 'Me', 0, 2, 'legacy words survive');
                INSERT INTO coaching(meeting_id, start_sec, original, corrected, ok, issues_json) VALUES(1, 1, 'a b c d', 'a b c d', 1, '[]');
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new MeetingDatabase(path);
        var old = db.GetMeeting(1)!;
        Assert.Equal("Old call", old.Title);
        Assert.Equal("{\"summary\":\"kept\"}", old.Summary);
        Assert.Equal("meeting", old.Kind);        // existing rows become plain meetings
        Assert.Null(old.VideoPath);
        Assert.Equal(0, old.SubtitleOffset);
        Assert.Single(db.Search("legacy"));
        Assert.Single(db.GetCoaching(1));

        var id = db.AddMeeting("New recording", DateTime.UtcNow, videoPath: @"C:\v\a.mp4", kind: "recording", source: "monitor 1");
        Assert.Equal(("recording", @"C:\v\a.mp4", "monitor 1"), (db.GetMeeting(id)!.Kind, db.GetMeeting(id)!.VideoPath, db.GetMeeting(id)!.Source));
        _ = new MeetingDatabase(path); // opening again is a no-op
        Assert.Equal(2, db.ListMeetings().Count);
    }

    [Fact]
    public void ListMeetings_CanFilterByKind_AndNewestFirst()
    {
        var db = new MeetingDatabase(NewPath());
        db.AddMeeting("Standup", DateTime.UtcNow.AddHours(-3));
        db.AddMeeting("Screen capture", DateTime.UtcNow.AddHours(-2), kind: "recording");
        db.AddMeeting("Lecture.mp4", DateTime.UtcNow.AddHours(-1), kind: "import");
        db.AddMeeting("Talk", DateTime.UtcNow, kind: "youtube", source: "https://youtu.be/abcdefghijk");

        Assert.Equal(new[] { "Talk", "Lecture.mp4", "Screen capture", "Standup" }, db.ListMeetings().Select(m => m.Title));
        Assert.Equal(new[] { "Standup" }, db.ListMeetings(kind: "meeting").Select(m => m.Title));
        Assert.Equal("https://youtu.be/abcdefghijk", Assert.Single(db.ListMeetings(kind: "youtube")).Source);
    }

    [Fact]
    public void VideoPath_AndTitle_CanBeUpdated()
    {
        var db = new MeetingDatabase(NewPath());
        var id = db.AddMeeting("meeting-20261106", DateTime.UtcNow, kind: "recording");
        db.SetVideoPath(id, @"C:\v\final.mp4");
        db.SetTitle(id, "Customer demo");
        db.SetSubtitleOffset(id, 0.35);
        Assert.Equal(0.35, db.GetMeeting(id)!.SubtitleOffset);
        var m = db.GetMeeting(id)!;
        Assert.Equal((@"C:\v\final.mp4", "Customer demo"), (m.VideoPath, m.Title));
    }

    [Fact]
    public void AiConversation_IsStoredInOrder_PerItem_AndClearable()
    {
        var db = new MeetingDatabase(NewPath());
        var a = db.AddMeeting("A", DateTime.UtcNow);
        var b = db.AddMeeting("B", DateTime.UtcNow);
        db.AddAiMessage(a, "user", "Summarize");
        db.AddAiMessage(a, "assistant", "It was about the budget.");
        db.AddAiMessage(b, "user", "Other question");

        var msgs = db.GetAiMessages(a);
        Assert.Equal(new[] { ("user", "Summarize"), ("assistant", "It was about the budget.") }, msgs.Select(m => (m.Role, m.Content)));
        Assert.True(msgs[0].CreatedUtc <= msgs[1].CreatedUtc);
        db.ClearAiMessages(a);
        Assert.Empty(db.GetAiMessages(a));
        Assert.Single(db.GetAiMessages(b));
    }

    [Fact]
    public void DeletingAnItem_RemovesItsTranscriptSearchEntriesAndConversation()
    {
        var db = new MeetingDatabase(NewPath());
        var id = db.AddMeeting("Throwaway", DateTime.UtcNow, kind: "recording");
        db.AddSegment(id, "Me", 0, 2, "unique zebra sentence");
        db.AddAiMessage(id, "user", "hi");
        Assert.Single(db.Search("zebra"));

        db.DeleteMeeting(id);
        Assert.Null(db.GetMeeting(id));
        Assert.Empty(db.Search("zebra"));
        Assert.Empty(db.GetAiMessages(id));
    }
}

public class SegmentEditTests
{
    [Fact]
    public void EditingALine_UpdatesSearch_AndDeletingRemovesIt()
    {
        var db = new MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-edit-" + Guid.NewGuid().ToString("N"), "t.db"));
        var id = db.AddMeeting("Call", DateTime.UtcNow, kind: "recording");
        db.AddSegment(id, "Me", 0, 2, "the quick brown fox");
        db.AddSegment(id, "Me", 2, 4, "jumps over the lazy dog");
        var first = db.GetTranscript(id)[0];

        db.UpdateSegmentText(first.Id, "the quick red panda");
        Assert.Equal("the quick red panda", db.GetTranscript(id)[0].Text);
        Assert.Empty(db.Search("fox"));                       // the old words are gone from the index
        Assert.Single(db.Search("panda"));                    // and the new ones are searchable
        Assert.Single(db.Search("lazy"));                     // other lines are untouched

        db.DeleteSegment(first.Id);
        Assert.Single(db.GetTranscript(id));
        Assert.Empty(db.Search("panda"));
    }
}
