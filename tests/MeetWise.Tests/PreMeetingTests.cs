using MeetWise.Intelligence;
using MeetWise.Storage;

namespace MeetWise.Tests;

public class PreMeetingTests
{
    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:single-1
        SUMMARY:Client kickoff
        DTSTART:20261110T140000Z
        DTEND:20261110T150000Z
        ATTENDEE;CN="Ana Silva":mailto:ana@example.com
        ATTENDEE:mailto:bob@example.com
        DESCRIPTION:Agenda: scope\, budget\nBring the contract
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        SUMMARY:Sprint review
        DTSTART:20261102T150000Z
        DTEND:20261102T153000Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,TH
        EXDATE:20261112T150000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:folded-1
        SUMMARY:A very long title that the calendar
          program folded onto a second line
        DTSTART;TZID=America/Sao_Paulo:20261111T100000
        DTEND;TZID=America/Sao_Paulo:20261111T110000
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled-1
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        DTSTART:20261110T160000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:daily-1
        SUMMARY:Daily standup
        DTSTART:20261109T090000Z
        DTEND:20261109T091500Z
        RRULE:FREQ=DAILY;COUNT=3
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly DateTime From = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_SingleEvent_WithAttendees_AndEscapes()
    {
        var e = IcsParser.Parse(Ics, From, To).Single(x => x.Uid == "single-1");
        Assert.Equal(new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc), e.StartUtc);
        Assert.Equal(new[] { "Ana Silva", "bob@example.com" }, e.Attendees);
        Assert.Contains("scope, budget\nBring", e.Description);
        Assert.False(e.IsRecurring);
    }

    [Fact]
    public void Expands_WeeklyByDay_AndHonorsExdate()
    {
        var sprints = IcsParser.Parse(Ics, From, To).Where(x => x.Uid == "weekly-1").Select(x => x.StartUtc.Date).ToList();
        // Mon 9, Thu 12 (excluded), Mon 16 is outside the window end (00:00) -> only Monday the 9th
        Assert.Equal(new[] { new DateTime(2026, 11, 9) }, sprints);
        var wide = IcsParser.Parse(Ics, From, To.AddDays(7)).Where(x => x.Uid == "weekly-1").Select(x => x.StartUtc.Date).ToList();
        Assert.Equal(new[] { new DateTime(2026, 11, 9), new DateTime(2026, 11, 16), new DateTime(2026, 11, 19) }, wide);
    }

    [Fact]
    public void Daily_Count_StopsAfterThree()
    {
        var days = IcsParser.Parse(Ics, From, To.AddDays(10)).Where(x => x.Uid == "daily-1").ToList();
        Assert.Equal(3, days.Count);
        Assert.Equal(new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc), days[^1].StartUtc);
    }

    [Fact]
    public void Handles_FoldedLines_TimeZones_AndCancelled()
    {
        var events = IcsParser.Parse(Ics, From, To);
        var folded = events.Single(x => x.Uid == "folded-1");
        Assert.Equal("A very long title that the calendar program folded onto a second line", folded.Title);
        // Sao Paulo is UTC-3 (no DST since 2019)
        Assert.Equal(new DateTime(2026, 11, 11, 13, 0, 0, DateTimeKind.Utc), folded.StartUtc);
        Assert.DoesNotContain(events, x => x.Uid == "cancelled-1");
    }

    [Fact]
    public void SeriesId_GroupsRecurringByUid_AndOneOffsByTitle()
    {
        var events = IcsParser.Parse(Ics, From, To.AddDays(7));
        var weekly = events.Where(x => x.Uid == "weekly-1").Select(x => x.SeriesId).Distinct().ToList();
        Assert.Equal(new[] { "ics:weekly-1" }, weekly);
        Assert.Equal("title:client kickoff", events.Single(x => x.Uid == "single-1").SeriesId);
    }

    [Fact]
    public void Reminder_FiresOnceInsideLeadWindow()
    {
        var evt = IcsParser.Parse(Ics, From, To).Single(x => x.Uid == "single-1");
        var sched = new ReminderScheduler(TimeSpan.FromMinutes(10));
        Assert.Empty(sched.Due(new[] { evt }, evt.StartUtc - TimeSpan.FromMinutes(30)));
        var due = sched.Due(new[] { evt }, evt.StartUtc - TimeSpan.FromMinutes(9));
        Assert.Equal(TimeSpan.FromMinutes(9), Assert.Single(due).Remaining);
        Assert.Empty(sched.Due(new[] { evt }, evt.StartUtc - TimeSpan.FromMinutes(5)));
        Assert.Empty(sched.Due(new[] { evt }, evt.StartUtc + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task CalendarService_FetchesWebcalAsHttps_AndCaches()
    {
        var h = new StubHandler { Respond = (_, _) => (System.Net.HttpStatusCode.OK, Ics) };
        var svc = new CalendarService(new HttpClient(h));
        var now = new DateTime(2026, 11, 10, 13, 0, 0, DateTimeKind.Utc);
        var events = await svc.UpcomingAsync("webcal://calendar.example/feed.ics", now, TimeSpan.FromHours(3));
        Assert.Contains(events, e => e.Uid == "single-1");
        await svc.UpcomingAsync("webcal://calendar.example/feed.ics", now.AddMinutes(1), TimeSpan.FromHours(3));
        Assert.Single(h.Calls);
        Assert.Equal("https://calendar.example/feed.ics", h.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Brief_UsesPreviousMeetingsInSameSeries_IncludingOpenActionItems()
    {
        var db = new MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-pm-" + Guid.NewGuid().ToString("N"), "t.db"));
        var evt = IcsParser.Parse(Ics, From, To).First(x => x.Uid == "weekly-1");
        var prev = db.AddMeeting("Sprint review", DateTime.UtcNow.AddDays(-7), evt.SeriesId);
        db.SetSummary(prev, MeetingIntelligence.Serialize(new MeetingNotes("Payments migration is blocked on QA.", [], [], [], [])));
        db.AddActionItem(prev, "Unblock QA environment", "Ana");
        db.AddMeeting("Unrelated", DateTime.UtcNow.AddDays(-1), "other");

        var llm = new FakeLlm { Reply = _ => "1. Payments status (10 min)" };
        var brief = await new PreMeetingAssistant(llm, db).BriefAsync(evt, new UserProfile("Leo", "tech lead"));
        Assert.Contains("Payments status", brief);
        Assert.Contains("Payments migration is blocked", llm.Prompts[0].User);
        Assert.Contains("OPEN: Unblock QA environment (Ana)", llm.Prompts[0].User);
        Assert.DoesNotContain("Unrelated", llm.Prompts[0].User);
        Assert.Contains("tech lead", llm.Prompts[0].System);
    }

    [Fact]
    public async Task Brief_FirstMeeting_SkipsLlm()
    {
        var db = new MeetingDatabase(Path.Combine(Path.GetTempPath(), "mw-pm-" + Guid.NewGuid().ToString("N"), "t.db"));
        var llm = new FakeLlm();
        var evt = IcsParser.Parse(Ics, From, To).Single(x => x.Uid == "single-1");
        var text = await new PreMeetingAssistant(llm, db).BriefAsync(evt);
        Assert.Contains("First meeting", text);
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public void Detector_PromptsOncePerMicSession_ForKnownAppsOnly()
    {
        var t0 = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        var state = new List<(string, bool, DateTime)> { ("C:#Program Files#Zoom#bin#Zoom.exe", true, t0), ("C:#tools#recorder.exe", true, t0) };
        var det = new MeetingDetector(() => state);
        var first = det.Poll();
        Assert.Equal("Zoom", first!.Process);
        Assert.Null(det.Poll());                       // same session: no repeat
        state[0] = (state[0].Item1, false, t0);        // call ends
        Assert.Null(det.Poll());
        state[0] = (state[0].Item1, true, t0.AddHours(2)); // next call
        Assert.NotNull(det.Poll());
    }

    [Fact]
    public void Detector_RealRegistry_DoesNotThrow()
    {
        var ex = Record.Exception(() => new MeetingDetector().Poll());
        Assert.Null(ex);
    }
}
