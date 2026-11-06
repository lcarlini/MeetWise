using System.Globalization;
using System.Text;
using Microsoft.Win32;
using MeetWise.Storage;

namespace MeetWise.Intelligence;

public sealed record Reminder(CalendarEvent Event, TimeSpan Remaining);

/// <summary>Decides which on-time notifications are due. Each occurrence is announced once.</summary>
public sealed class ReminderScheduler
{
    private readonly HashSet<string> _announced = new();
    private readonly TimeSpan _lead;

    public ReminderScheduler(TimeSpan lead) => _lead = lead;

    public IReadOnlyList<Reminder> Due(IEnumerable<CalendarEvent> upcoming, DateTime nowUtc)
    {
        var due = new List<Reminder>();
        foreach (var e in upcoming)
        {
            var remaining = e.StartUtc - nowUtc;
            if (remaining > _lead || remaining < TimeSpan.FromMinutes(-2)) continue;
            if (_announced.Add(e.Uid + "@" + e.StartUtc.Ticks)) due.Add(new Reminder(e, remaining));
        }
        return due;
    }
}

/// <summary>Builds the pre-meeting brief and suggested agenda from what happened in earlier meetings of the same series.</summary>
public sealed class PreMeetingAssistant
{
    private readonly ILlmProvider _llm;
    private readonly MeetingDatabase _db;

    public PreMeetingAssistant(ILlmProvider llm, MeetingDatabase db)
    {
        _llm = llm;
        _db = db;
    }

    /// <summary>Raw context shown even without an LLM: previous meetings, their summaries and open action items.</summary>
    public string BuildContext(CalendarEvent evt, int maxPrevious = 3)
    {
        var sb = new StringBuilder();
        var previous = _db.ListMeetings(seriesId: evt.SeriesId, limit: maxPrevious);
        foreach (var m in previous)
        {
            sb.AppendLine($"## {m.Title} ({m.StartedUtc:yyyy-MM-dd})");
            string? summary = null;
            if (m.Summary is not null)
            {
                try { summary = MeetingIntelligence.ParseNotes(m.Summary).Summary; } catch (FormatException) { }
            }
            sb.AppendLine(summary ?? "(no notes were generated)");
            foreach (var a in _db.ActionItemsFor(m.Id).Where(a => !a.Done))
                sb.AppendLine($"- OPEN: {a.Text}{(a.Owner is null ? "" : " (" + a.Owner + ")")}");
        }
        return sb.ToString();
    }

    public async Task<string> BriefAsync(CalendarEvent evt, UserProfile? profile = null, CancellationToken ct = default)
    {
        var context = BuildContext(evt);
        if (context.Length == 0)
            return $"First meeting of \"{evt.Title}\" that MeetWise knows about. Attendees: {string.Join(", ", evt.Attendees)}.";

        var system = "You prepare people for meetings. Using only the history provided, write: (1) a three-line brief of where things stand, "
            + "(2) open items that must be followed up, (3) a suggested agenda as a numbered list with rough minutes. Be concise."
            + (profile?.Occupation is { } o ? $" The reader is {profile.Nickname ?? "the user"}, {o}." : "");
        var user = $"Upcoming meeting: {evt.Title} on {evt.StartUtc:yyyy-MM-dd HH:mm} UTC.\nAttendees: {string.Join(", ", evt.Attendees)}\n"
            + (evt.Description is null ? "" : $"Invite text: {evt.Description}\n") + $"\nHISTORY:\n{context}";
        return (await _llm.CompleteAsync(system, new[] { new ChatMessage("user", user) }, 700, ct)).Trim();
    }
}

public sealed record DetectedMeeting(string Process, DateTime SinceUtc);

/// <summary>
/// Notices when a conferencing app starts using the microphone, using the Windows privacy consent store
/// (the same data behind the orange microphone indicator). No audio is accessed.
/// </summary>
public sealed class MeetingDetector
{
    public static readonly string[] KnownApps =
    {
        "zoom", "teams", "ms-teams", "slack", "webex", "skype", "discord", "chrome", "msedge", "firefox", "brave", "whatsapp", "telegram", "gotomeeting"
    };

    private readonly Func<IEnumerable<(string Process, bool InUse, DateTime LastStartUtc)>> _source;
    private readonly HashSet<string> _seen = new();

    public MeetingDetector(Func<IEnumerable<(string Process, bool InUse, DateTime LastStartUtc)>>? source = null)
        => _source = source ?? ReadConsentStore;

    /// <summary>Returns a meeting once per microphone session (when a known app starts using the mic).</summary>
    public DetectedMeeting? Poll()
    {
        DetectedMeeting? found = null;
        var active = new HashSet<string>();
        foreach (var (process, inUse, start) in _source())
        {
            if (!inUse || !IsKnown(process)) continue;
            var id = process + "@" + start.Ticks;
            active.Add(id);
            if (_seen.Add(id) && found is null) found = new DetectedMeeting(Friendly(process), start);
        }
        _seen.IntersectWith(active); // forget sessions that ended so the next call prompts again
        return found;
    }

    public static bool IsKnown(string process)
        => KnownApps.Any(a => process.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static string Friendly(string process)
    {
        var name = Path.GetFileNameWithoutExtension(process.Replace('#', '\\'));
        return name.Length == 0 ? process : name;
    }

    private static IEnumerable<(string Process, bool InUse, DateTime LastStartUtc)> ReadConsentStore()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        var results = new List<(string, bool, DateTime)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(root + @"\NonPackaged");
            if (key is null) return results;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStart") is not long start || app.GetValue("LastUsedTimeStop") is not long stop) continue;
                results.Add((name, stop == 0 && start > 0, start > 0 ? DateTime.FromFileTimeUtc(start) : DateTime.MinValue));
            }
        }
        catch (Exception) { /* registry unavailable: no detection */ }
        return results;
    }
}
