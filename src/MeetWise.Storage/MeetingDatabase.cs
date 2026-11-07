using Microsoft.Data.Sqlite;

namespace MeetWise.Storage;

public sealed record Meeting(long Id, string Title, DateTime StartedUtc, string? SeriesId, string? Folder, string? AudioPath, string? Summary,
    string? VideoPath = null, string Kind = "meeting", string? Source = null, double SubtitleOffset = 0);
public sealed record AiMessage(long Id, long MeetingId, string Role, string Content, DateTime CreatedUtc);
public sealed record TranscriptSegment(long Id, long MeetingId, string Speaker, double StartSec, double EndSec, string Text, string? Language);
public sealed record CoachingEntry(long Id, long MeetingId, double StartSec, string Original, string Corrected, bool Ok, string IssuesJson, double? Wpm);
public sealed record ActionItem(long Id, long MeetingId, string Text, string? Owner, DateTime? DueUtc, bool Done);

/// <summary>SQLite store with FTS5 transcript search. One connection per call; WAL for crash safety.</summary>
public sealed class MeetingDatabase
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;

    public MeetingDatabase(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var c = Open();
        long version;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = (long)cmd.ExecuteScalar()!;
        }
        if (version >= SchemaVersion) return;
        if (version < 1) Exec(c, """
            CREATE TABLE meetings(
              id INTEGER PRIMARY KEY, title TEXT NOT NULL, started_utc TEXT NOT NULL,
              series_id TEXT, folder TEXT, audio_path TEXT, summary TEXT);
            CREATE TABLE segments(
              id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              speaker TEXT NOT NULL, start_sec REAL NOT NULL, end_sec REAL NOT NULL, text TEXT NOT NULL, language TEXT);
            CREATE INDEX ix_segments_meeting ON segments(meeting_id, start_sec);
            CREATE VIRTUAL TABLE segments_fts USING fts5(text, content='segments', content_rowid='id');
            CREATE TRIGGER segments_ai AFTER INSERT ON segments BEGIN
              INSERT INTO segments_fts(rowid, text) VALUES (new.id, new.text); END;
            CREATE TRIGGER segments_ad AFTER DELETE ON segments BEGIN
              INSERT INTO segments_fts(segments_fts, rowid, text) VALUES('delete', old.id, old.text); END;
            CREATE TABLE action_items(
              id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              text TEXT NOT NULL, owner TEXT, due_utc TEXT, done INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE tags(meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY(meeting_id, tag));
            """);
        // v2: speaking-coach feedback per spoken sentence
        if (version < 2) Exec(c, """
            CREATE TABLE coaching(
              id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              start_sec REAL NOT NULL, original TEXT NOT NULL, corrected TEXT NOT NULL, ok INTEGER NOT NULL,
              issues_json TEXT NOT NULL, wpm REAL);
            CREATE INDEX ix_coaching_meeting ON coaching(meeting_id, start_sec);
            """);
        // v3: Studio items (recordings, imports, YouTube) with their video file, and the AI conversation per item
        if (version < 3) Exec(c, """
            ALTER TABLE meetings ADD COLUMN video_path TEXT;
            ALTER TABLE meetings ADD COLUMN kind TEXT NOT NULL DEFAULT 'meeting';
            ALTER TABLE meetings ADD COLUMN source TEXT;
            ALTER TABLE meetings ADD COLUMN subtitle_offset REAL NOT NULL DEFAULT 0;
            CREATE TABLE ai_messages(
              id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
              role TEXT NOT NULL, content TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE INDEX ix_ai_meeting ON ai_messages(meeting_id, id);
            """);
        Exec(c, $"PRAGMA user_version={SchemaVersion};");
    }

    public long AddMeeting(string title, DateTime startedUtc, string? seriesId = null, string? folder = null, string? audioPath = null,
        string? videoPath = null, string kind = "meeting", string? source = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meetings(title, started_utc, series_id, folder, audio_path, video_path, kind, source) VALUES($t,$s,$r,$f,$a,$v,$k,$o); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$s", startedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$r", (object?)seriesId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)audioPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$v", (object?)videoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$o", (object?)source ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void AddSegment(long meetingId, string speaker, double start, double end, string text, string? language = null)
    {
        using var c = Open();
        Exec(c, "INSERT INTO segments(meeting_id, speaker, start_sec, end_sec, text, language) VALUES($m,$s,$a,$b,$t,$l)",
            ("$m", meetingId), ("$s", speaker), ("$a", start), ("$b", end), ("$t", text), ("$l", language));
    }

    public void AddActionItem(long meetingId, string text, string? owner = null, DateTime? dueUtc = null)
    {
        using var c = Open();
        Exec(c, "INSERT INTO action_items(meeting_id, text, owner, due_utc) VALUES($m,$t,$o,$d)",
            ("$m", meetingId), ("$t", text), ("$o", owner), ("$d", dueUtc?.ToString("o")));
    }

    public IReadOnlyList<TranscriptSegment> GetTranscript(long meetingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, meeting_id, speaker, start_sec, end_sec, text, language FROM segments WHERE meeting_id=$m ORDER BY start_sec";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<TranscriptSegment>();
        while (r.Read())
            list.Add(new TranscriptSegment(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    /// <summary>Full-text search across all meetings. Query is treated as plain words (quoted), not FTS syntax.</summary>
    public IReadOnlyList<TranscriptSegment> Search(string query, int limit = 50)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"");
        var match = string.Join(" ", terms);
        if (match.Length == 0) return Array.Empty<TranscriptSegment>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.meeting_id, s.speaker, s.start_sec, s.end_sec, s.text, s.language
            FROM segments_fts f JOIN segments s ON s.id = f.rowid
            WHERE segments_fts MATCH $q ORDER BY rank LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TranscriptSegment>();
        while (r.Read())
            list.Add(new TranscriptSegment(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    public void AddCoaching(long meetingId, double startSec, string original, string corrected, bool ok, string issuesJson, double? wpm)
    {
        using var c = Open();
        Exec(c, "INSERT INTO coaching(meeting_id, start_sec, original, corrected, ok, issues_json, wpm) VALUES($m,$s,$o,$c,$k,$i,$w)",
            ("$m", meetingId), ("$s", startSec), ("$o", original), ("$c", corrected), ("$k", ok ? 1 : 0), ("$i", issuesJson), ("$w", wpm));
    }

    public IReadOnlyList<CoachingEntry> GetCoaching(long meetingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, meeting_id, start_sec, original, corrected, ok, issues_json, wpm FROM coaching WHERE meeting_id=$m ORDER BY start_sec";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<CoachingEntry>();
        while (r.Read())
            list.Add(new CoachingEntry(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetString(3), r.GetString(4), r.GetInt32(5) != 0, r.GetString(6), r.IsDBNull(7) ? null : r.GetDouble(7)));
        return list;
    }

    public void SetVideoPath(long meetingId, string? videoPath)
    {
        using var c = Open();
        Exec(c, "UPDATE meetings SET video_path=$v WHERE id=$m", ("$v", videoPath), ("$m", meetingId));
    }

    /// <summary>Seconds to add to subtitle times so they line up with the video (the audio starts a little after the screen capture).</summary>
    public void SetSubtitleOffset(long meetingId, double seconds)
    {
        using var c = Open();
        Exec(c, "UPDATE meetings SET subtitle_offset=$o WHERE id=$m", ("$o", seconds), ("$m", meetingId));
    }

    public void SetTitle(long meetingId, string title)
    {
        using var c = Open();
        Exec(c, "UPDATE meetings SET title=$t WHERE id=$m", ("$t", title), ("$m", meetingId));
    }

    /// <summary>Removes the item and (through the cascade) its transcript, notes and AI conversation. Files on disk are not touched.</summary>
    public void DeleteMeeting(long meetingId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM meetings WHERE id=$m", ("$m", meetingId));
    }

    public void AddAiMessage(long meetingId, string role, string content)
    {
        using var c = Open();
        Exec(c, "INSERT INTO ai_messages(meeting_id, role, content, created_utc) VALUES($m,$r,$c,$t)",
            ("$m", meetingId), ("$r", role), ("$c", content), ("$t", DateTime.UtcNow.ToString("o")));
    }

    public IReadOnlyList<AiMessage> GetAiMessages(long meetingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, meeting_id, role, content, created_utc FROM ai_messages WHERE meeting_id=$m ORDER BY id";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<AiMessage>();
        while (r.Read())
            list.Add(new AiMessage(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return list;
    }

    public void ClearAiMessages(long meetingId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM ai_messages WHERE meeting_id=$m", ("$m", meetingId));
    }

    /// <summary>Edits one subtitle line. The full-text index is rebuilt for that row so search finds the corrected text.</summary>
    public void UpdateSegmentText(long segmentId, string text)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "INSERT INTO segments_fts(segments_fts, rowid, text) SELECT 'delete', id, text FROM segments WHERE id=$i", ("$i", segmentId));
        Exec(c, "UPDATE segments SET text=$t WHERE id=$i", ("$t", text), ("$i", segmentId));
        Exec(c, "INSERT INTO segments_fts(rowid, text) SELECT id, text FROM segments WHERE id=$i", ("$i", segmentId));
        tx.Commit();
    }

    public void DeleteSegment(long segmentId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM segments WHERE id=$i", ("$i", segmentId)); // the delete trigger keeps the search index in sync
    }

    public void SetSegmentSpeaker(long segmentId, string speaker)
    {
        using var c = Open();
        Exec(c, "UPDATE segments SET speaker=$s WHERE id=$i", ("$s", speaker), ("$i", segmentId));
    }

    /// <summary>Replaces a meeting's transcript (used by the high-accuracy second pass). FTS stays in sync through the triggers.</summary>
    public void ReplaceSegments(long meetingId, IEnumerable<(string Speaker, double Start, double End, string Text, string? Language)> segments)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM segments WHERE meeting_id=$m", ("$m", meetingId));
        foreach (var s in segments)
            Exec(c, "INSERT INTO segments(meeting_id, speaker, start_sec, end_sec, text, language) VALUES($m,$s,$a,$b,$t,$l)",
                ("$m", meetingId), ("$s", s.Speaker), ("$a", s.Start), ("$b", s.End), ("$t", s.Text), ("$l", s.Language));
        tx.Commit();
    }

    public void SetSummary(long meetingId, string summaryJson)
    {
        using var c = Open();
        Exec(c, "UPDATE meetings SET summary=$s WHERE id=$m", ("$s", summaryJson), ("$m", meetingId));
    }

    public void SetActionItemDone(long id, bool done)
    {
        using var c = Open();
        Exec(c, "UPDATE action_items SET done=$d WHERE id=$i", ("$d", done ? 1 : 0), ("$i", id));
    }

    public void AddTag(long meetingId, string tag)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO tags(meeting_id, tag) VALUES($m,$t)", ("$m", meetingId), ("$t", tag.Trim().ToLowerInvariant()));
    }

    public void MoveToFolder(long meetingId, string? folder)
    {
        using var c = Open();
        Exec(c, "UPDATE meetings SET folder=$f WHERE id=$m", ("$f", folder), ("$m", meetingId));
    }

    private static Meeting ReadMeeting(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1),
        DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetDouble(10));

    public Meeting? GetMeeting(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title, started_utc, series_id, folder, audio_path, summary, video_path, kind, source, subtitle_offset FROM meetings WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMeeting(r) : null;
    }

    /// <summary>Meetings newest first; optionally only one recurring series, folder or tag.</summary>
    public IReadOnlyList<Meeting> ListMeetings(string? seriesId = null, string? folder = null, string? tag = null, int limit = 200, string? kind = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT id, title, started_utc, series_id, folder, audio_path, summary, video_path, kind, source, subtitle_offset FROM meetings
            WHERE ($s IS NULL OR series_id=$s) AND ($f IS NULL OR folder=$f) AND ($k IS NULL OR kind=$k)
              AND ($t IS NULL OR id IN (SELECT meeting_id FROM tags WHERE tag=$t))
            ORDER BY started_utc DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$s", (object?)seriesId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)tag?.Trim().ToLowerInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Meeting>();
        while (r.Read()) list.Add(ReadMeeting(r));
        return list;
    }

    /// <summary>Retrieval for Ask MeetWise: any of the keywords may match (OR), best BM25 first, optionally within one meeting.</summary>
    public IReadOnlyList<TranscriptSegment> SearchAny(IEnumerable<string> keywords, long? meetingId = null, int limit = 12)
    {
        var terms = keywords.Select(k => k.Trim()).Where(k => k.Length > 1)
            .Select(k => "\"" + k.Replace("\"", "\"\"") + "\"").Distinct().ToList();
        if (terms.Count == 0) return Array.Empty<TranscriptSegment>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT s.id, s.meeting_id, s.speaker, s.start_sec, s.end_sec, s.text, s.language
            FROM segments_fts f JOIN segments s ON s.id = f.rowid
            WHERE segments_fts MATCH $q AND ($m IS NULL OR s.meeting_id=$m) ORDER BY rank LIMIT $n";
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", terms));
        cmd.Parameters.AddWithValue("$m", (object?)meetingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TranscriptSegment>();
        while (r.Read())
            list.Add(new TranscriptSegment(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    public IReadOnlyList<ActionItem> ActionItemsFor(long meetingId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, meeting_id, text, owner, due_utc, done FROM action_items WHERE meeting_id=$m ORDER BY id";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<ActionItem>();
        while (r.Read())
            list.Add(new ActionItem(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetInt32(5) != 0));
        return list;
    }

    public IReadOnlyList<ActionItem> OpenActionItems()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, meeting_id, text, owner, due_utc, done FROM action_items WHERE done=0 ORDER BY due_utc IS NULL, due_utc";
        using var r = cmd.ExecuteReader();
        var list = new List<ActionItem>();
        while (r.Read())
            list.Add(new ActionItem(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetInt32(5) != 0));
        return list;
    }
}
