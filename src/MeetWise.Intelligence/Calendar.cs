using System.Globalization;
using System.Text;

namespace MeetWise.Intelligence;

public sealed record CalendarEvent(string Uid, string Title, DateTime StartUtc, DateTime EndUtc,
    IReadOnlyList<string> Attendees, string? Description, bool IsRecurring)
{
    /// <summary>Stable id shared by every occurrence of a recurring meeting; used to group meetings into a series.</summary>
    public string SeriesId => IsRecurring ? "ics:" + Uid : "title:" + NormalizeTitle(Title);

    public static string NormalizeTitle(string title)
        => string.Join(' ', title.ToLowerInvariant().Split(new[] { ' ', '\t', '-', ':', '|' }, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Reads iCalendar (.ics) feeds, which Google Calendar ("secret address in iCal format"), Outlook and Apple Calendar all publish.</summary>
public static class IcsParser
{
    public static IReadOnlyList<CalendarEvent> Parse(string ics, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<CalendarEvent>();
        foreach (var block in Blocks(Unfold(ics)))
        {
            var props = block.Where(l => l.Contains(':')).Select(ParseLine).ToList();
            string? Get(string name) => props.FirstOrDefault(p => p.Name == name).Value;
            (string Name, Dictionary<string, string> Params, string Value) Prop(string name) => props.FirstOrDefault(p => p.Name == name);

            var startProp = Prop("DTSTART");
            if (startProp.Name is null) continue;
            if (Get("STATUS") == "CANCELLED") continue;
            var start = ParseDate(startProp.Value, startProp.Params, out bool allDay);
            if (start is null) continue;
            var endProp = Prop("DTEND");
            DateTime end = endProp.Name is not null && ParseDate(endProp.Value, endProp.Params, out _) is { } e
                ? e : start.Value.AddHours(allDay ? 24 : 1);

            var attendees = props.Where(p => p.Name == "ATTENDEE")
                .Select(p => p.Params.TryGetValue("CN", out var cn) ? cn.Trim('"') : p.Value.Replace("mailto:", "", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var rrule = Get("RRULE");
            var exdates = props.Where(p => p.Name == "EXDATE").SelectMany(p => p.Value.Split(','))
                .Select(v => ParseDate(v, props.First(x => x.Name == "EXDATE").Params, out _)).Where(d => d is not null).Select(d => d!.Value).ToHashSet();

            var evt = new CalendarEvent(Get("UID") ?? Guid.NewGuid().ToString(), Unescape(Get("SUMMARY") ?? "(no title)"),
                start.Value, end, attendees, Get("DESCRIPTION") is { } d ? Unescape(d) : null, rrule is not null);

            foreach (var occ in Expand(evt, rrule, exdates, fromUtc, toUtc))
                result.Add(occ);
        }
        return result.OrderBy(e => e.StartUtc).ToList();
    }

    private static IEnumerable<CalendarEvent> Expand(CalendarEvent first, string? rrule, HashSet<DateTime> exdates, DateTime from, DateTime to)
    {
        var duration = first.EndUtc - first.StartUtc;
        if (rrule is null)
        {
            if (first.EndUtc >= from && first.StartUtc <= to) yield return first;
            yield break;
        }

        var rule = rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
        var freq = rule.GetValueOrDefault("FREQ", "WEEKLY");
        int interval = rule.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var ivn) && ivn > 0 ? ivn : 1;
        int? count = rule.TryGetValue("COUNT", out var cv) && int.TryParse(cv, out var cn) ? cn : null;
        DateTime? until = rule.TryGetValue("UNTIL", out var uv) ? ParseDate(uv, new(), out _) : null;
        var byDay = rule.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(d => d.Length >= 2 ? DayOf(d[^2..]) : null).Where(d => d is not null).Select(d => d!.Value).ToList()
            : new List<DayOfWeek>();

        int emitted = 0;
        var cursor = first.StartUtc;
        for (int guard = 0; guard < 5000; guard++)
        {
            IEnumerable<DateTime> candidates;
            if (freq == "WEEKLY" && byDay.Count > 0)
            {
                var weekStart = cursor.Date.AddDays(-(int)cursor.DayOfWeek);
                candidates = byDay.OrderBy(d => (int)d).Select(d => weekStart.AddDays((int)d) + cursor.TimeOfDay).Where(d => d >= first.StartUtc);
            }
            else candidates = new[] { cursor };

            foreach (var occ in candidates)
            {
                if (until is not null && occ > until) yield break;
                if (count is not null && emitted >= count) yield break;
                emitted++;
                if (occ > to) yield break;
                if (exdates.Contains(occ)) continue;
                if (occ + duration >= from) yield return first with { StartUtc = occ, EndUtc = occ + duration };
            }

            cursor = freq switch
            {
                "DAILY" => cursor.AddDays(interval),
                "WEEKLY" => cursor.AddDays(7 * interval),
                "MONTHLY" => cursor.AddMonths(interval),
                "YEARLY" => cursor.AddYears(interval),
                _ => cursor.AddDays(7 * interval)
            };
            if (cursor > to && (until is null || cursor > until)) yield break;
        }
    }

    private static DayOfWeek? DayOf(string code) => code.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null
    };

    private static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith(' ') || raw.StartsWith('\t')) { sb.Append(raw.AsSpan(1)); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static IEnumerable<List<string>> Blocks(IEnumerable<string> lines)
    {
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) current = new();
            else if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (current is not null) yield return current; current = null; }
            else current?.Add(line);
        }
    }

    private static (string Name, Dictionary<string, string> Params, string Value) ParseLine(string line)
    {
        int colon = line.IndexOf(':');
        var head = line[..colon].Split(';');
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            var kv = p.Split('=', 2);
            if (kv.Length == 2) ps[kv[0]] = kv[1];
        }
        return (head[0].ToUpperInvariant(), ps, line[(colon + 1)..]);
    }

    internal static DateTime? ParseDate(string value, Dictionary<string, string> ps, out bool allDay)
    {
        allDay = false;
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            allDay = true;
            return DateTime.SpecifyKind(day, DateTimeKind.Utc);
        }
        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return utc;
        if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            TimeZoneInfo zone = TimeZoneInfo.Local;
            if (ps.TryGetValue("TZID", out var tzid))
            {
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(tzid.Trim('"')); } catch (TimeZoneNotFoundException) { }
            }
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
        }
        return null;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
}

/// <summary>Downloads and caches an ICS feed and answers "what is coming up".</summary>
public sealed class CalendarService
{
    private readonly HttpClient _http;
    private readonly TimeSpan _refresh;
    private string? _ics;
    private DateTime _fetched = DateTime.MinValue;

    public CalendarService(HttpClient http, TimeSpan? refresh = null)
    {
        _http = http;
        _refresh = refresh ?? TimeSpan.FromMinutes(15);
    }

    public async Task<IReadOnlyList<CalendarEvent>> UpcomingAsync(string feedUrl, DateTime nowUtc, TimeSpan window, CancellationToken ct = default)
    {
        if (_ics is null || nowUtc - _fetched > _refresh)
        {
            // webcal:// is just https:// for calendar apps
            var url = feedUrl.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + feedUrl[9..] : feedUrl;
            _ics = await _http.GetStringAsync(url, ct);
            _fetched = nowUtc;
        }
        return IcsParser.Parse(_ics, nowUtc, nowUtc + window).Where(e => e.StartUtc >= nowUtc - TimeSpan.FromMinutes(5)).ToList();
    }
}
