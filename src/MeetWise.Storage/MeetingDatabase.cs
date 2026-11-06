using Microsoft.Data.Sqlite;

namespace MeetWise.Storage;

public sealed record Meeting(long Id, string Title, DateTime StartedUtc, string? SeriesId, string? Folder, string? AudioPath);
public sealed record TranscriptSegment(long Id, long MeetingId, string Speaker, double StartSec, double EndSec, string Text, string? Language);
public sealed record ActionItem(long Id, long MeetingId, string Text, string? Owner, DateTime? DueUtc, bool Done);

/// <summary>SQLite store with FTS5 transcript search. One connection per call; WAL for crash safety.</summary>
public sealed class MeetingDatabase
{
    private const int SchemaVersion = 1;
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
        Exec(c, """
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
        Exec(c, $"PRAGMA user_version={SchemaVersion};");
    }

    public long AddMeeting(string title, DateTime startedUtc, string? seriesId = null, string? folder = null, string? audioPath = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meetings(title, started_utc, series_id, folder, audio_path) VALUES($t,$s,$r,$f,$a); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$s", startedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$r", (object?)seriesId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)audioPath ?? DBNull.Value);
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
