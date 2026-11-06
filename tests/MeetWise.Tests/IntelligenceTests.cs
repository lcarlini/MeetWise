using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MeetWise.Intelligence;
using MeetWise.Storage;

namespace MeetWise.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();
    public Func<HttpRequestMessage, string, (HttpStatusCode, string)> Respond { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        var (status, json) = Respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

internal sealed class FakeLlm : ILlmProvider
{
    public string Name => "fake";
    public int ContextTokens { get; set; } = 8000;
    public List<(string System, string User)> Prompts { get; } = new();
    public Func<string, string> Reply { get; set; } = _ => "{}";

    public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
    {
        var user = messages[^1].Content;
        Prompts.Add((system, user));
        return Task.FromResult(Reply(user));
    }
}

public class IntelligenceTests
{
    private const string NotesJson = """
        Sure! Here are the notes:
        ```json
        {"summary":"Team agreed to ship invoice export Friday.","key_points":["Invoice export","Kubernetes upgrade"],
         "decisions":["Ship Friday"],"risks":["Manifests not updated"],
         "action_items":[{"text":"Update manifests","owner":"Ana","due":"2026-11-13"},{"text":"Write release notes","owner":null,"due":null}]}
        ```
        """;

    private static MeetingDatabase NewDb() => new(Path.Combine(Path.GetTempPath(), "mw-int-" + Guid.NewGuid().ToString("N"), "t.db"));

    [Fact]
    public void ParseNotes_ExtractsJsonFromChattyReply()
    {
        var n = MeetingIntelligence.ParseNotes(NotesJson);
        Assert.Contains("invoice export", n.Summary);
        Assert.Equal(2, n.KeyPoints.Count);
        Assert.Equal(2, n.ActionItems.Count);
        Assert.Equal("Ana", n.ActionItems[0].Owner);
        Assert.Equal(new DateTime(2026, 11, 13), n.ActionItems[0].DueUtc!.Value.Date);
        Assert.Null(n.ActionItems[1].Owner);
    }

    [Fact]
    public void ParseNotes_ThrowsWhenNoJson() => Assert.Throws<FormatException>(() => MeetingIntelligence.ParseNotes("I cannot do that."));

    [Fact]
    public void ExtractJson_HandlesBracesInsideStrings()
    {
        var json = MeetingIntelligence.ExtractJsonObject("x {\"summary\":\"use {braces} and \\\"quotes\\\"\"} y");
        Assert.NotNull(json);
        Assert.Contains("braces", JsonNode.Parse(json!)!["summary"]!.ToString());
    }

    [Fact]
    public async Task GenerateNotes_UsesTemplateAndProfile_AndSavesActionItems()
    {
        var llm = new FakeLlm { Reply = _ => NotesJson };
        var intel = new MeetingIntelligence(llm);
        var notes = await intel.GenerateNotesAsync("[00:00:01] Me: ship friday", "incident",
            new UserProfile("Leo", "engineering manager", "Leads the payments team."), new DateTime(2026, 11, 6));
        Assert.Contains("Incident review", llm.Prompts[0].System);
        Assert.Contains("engineering manager", llm.Prompts[0].System);
        Assert.Contains("2026-11-06", llm.Prompts[0].User);

        var db = NewDb();
        var id = db.AddMeeting("Incident", DateTime.UtcNow);
        MeetingIntelligence.Save(db, id, notes);
        Assert.Equal(2, db.ActionItemsFor(id).Count);
        Assert.Contains("Ship Friday", db.GetMeeting(id)!.Summary);
    }

    [Fact]
    public async Task LongTranscript_IsChunked_ThenMerged()
    {
        var calls = 0;
        var llm = new FakeLlm { ContextTokens = 1000, Reply = _ => { calls++; return NotesJson; } };
        var transcript = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"[00:00:{i % 60:00}] Others: line number {i} about the roadmap"));
        var notes = await new MeetingIntelligence(llm).GenerateNotesAsync(transcript);
        Assert.True(calls >= 3, $"expected map + merge calls, got {calls}");
        Assert.Contains(llm.Prompts, p => p.User.StartsWith("Merge these partial notes"));
        Assert.NotEmpty(notes.ActionItems);
    }

    [Fact]
    public async Task AnthropicProvider_SendsKeyAndParsesText()
    {
        var h = new StubHandler
        {
            Respond = (_, _) => (HttpStatusCode.OK, """{"content":[{"type":"text","text":"Hello "},{"type":"text","text":"world"}]}""")
        };
        var p = new AnthropicProvider(new HttpClient(h), "sk-test", "claude-haiku-5-5");
        var text = await p.CompleteAsync("sys", new[] { new ChatMessage("user", "hi") });
        Assert.Equal("Hello world", text);
        var call = h.Calls.Single();
        Assert.Equal("sk-test", call.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://api.anthropic.com/v1/messages", call.Request.RequestUri!.ToString());
        var body = JsonNode.Parse(call.Body)!;
        Assert.Equal("claude-haiku-5-5", body["model"]!.ToString());
        Assert.Equal("sys", body["system"]!.ToString());
    }

    [Fact]
    public async Task OpenAiCompatible_WorksWithoutKey_ForLocalServers()
    {
        var h = new StubHandler { Respond = (_, _) => (HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""") };
        var p = new OpenAiCompatibleProvider(new HttpClient(h), "http://localhost:11434/v1", "qwen3:8b");
        Assert.Equal("ok", await p.CompleteAsync("s", new[] { new ChatMessage("user", "q") }));
        Assert.Null(h.Calls[0].Request.Headers.Authorization);
        Assert.Equal("http://localhost:11434/v1/chat/completions", h.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Provider_SurfacesHttpErrors()
    {
        var h = new StubHandler { Respond = (_, _) => (HttpStatusCode.Unauthorized, """{"error":"bad key"}""") };
        var p = new AnthropicProvider(new HttpClient(h), "bad");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.CompleteAsync("s", new[] { new ChatMessage("user", "q") }));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Ask_RetrievesRelevantSegments_AndCites()
    {
        var db = NewDb();
        var m1 = db.AddMeeting("Sprint", DateTime.UtcNow);
        db.AddSegment(m1, "Others", 65, 70, "Ana will update the Kubernetes manifests by Friday");
        db.AddSegment(m1, "Me", 80, 85, "Lunch is at noon");
        var llm = new FakeLlm { Reply = _ => "Ana [meeting 1 @ 00:01:05]" };
        var ans = await new AskMeetWise(llm, db).AskAsync("Who is updating the kubernetes manifests?");
        Assert.Single(ans.Sources);
        Assert.Contains("Kubernetes manifests", llm.Prompts[0].User);
        Assert.DoesNotContain("Lunch", llm.Prompts[0].User);
        Assert.Contains("[meeting 1 @ 00:01:05]", ans.Text);
    }

    [Fact]
    public async Task Ask_NoHits_DoesNotCallLlm()
    {
        var llm = new FakeLlm();
        var ans = await new AskMeetWise(llm, NewDb()).AskAsync("What about unicorns?");
        Assert.Empty(llm.Prompts);
        Assert.Empty(ans.Sources);
    }

    [Fact]
    public void Glossary_FixesNearMisses_AndLeavesOtherWords()
    {
        var g = new Glossary(Glossary.StarterKits["software"].Concat(new[] { "Carlini" }));
        Assert.Equal("We deploy on Kubernetes with PostgreSQL", g.Apply("We deploy on Kubernetis with Postgresql"));
        Assert.Equal("Hi Carlini", g.Apply("Hi Carlino"));
        Assert.Equal("the plan is fine", g.Apply("the plan is fine"));
    }

    [Fact]
    public void SecretStore_RoundTrips_AndIsNotPlaintextOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-sec-" + Guid.NewGuid().ToString("N"));
        var s = new SecretStore(dir);
        s.Set("anthropic", "sk-ant-very-secret");
        Assert.Equal("sk-ant-very-secret", s.Get("anthropic"));
        foreach (var f in Directory.GetFiles(dir))
            Assert.DoesNotContain("very-secret", File.ReadAllText(f, Encoding.Latin1));
        s.Remove("anthropic");
        Assert.Null(s.Get("anthropic"));
    }

    [Fact]
    public async Task Webhook_PostsActionItems()
    {
        var h = new StubHandler();
        var meeting = new Meeting(7, "Sprint", DateTime.UtcNow, null, null, null, null);
        await new WebhookExporter(new HttpClient(h)).ExportAsync("https://example.test/hook", meeting,
            new[] { new ActionItem(1, 7, "Update manifests", "Ana", null, false) });
        var body = JsonNode.Parse(h.Calls.Single().Body)!;
        Assert.Equal("Sprint", body["meeting"]!["title"]!.ToString());
        Assert.Equal("Ana", body["actionItems"]![0]!["owner"]!.ToString());
    }

    [Fact]
    public void Database_ListMeetings_FiltersBySeriesFolderTag()
    {
        var db = NewDb();
        var a = db.AddMeeting("Weekly 1", DateTime.UtcNow.AddDays(-7), "weekly");
        var b = db.AddMeeting("Weekly 2", DateTime.UtcNow, "weekly");
        var c = db.AddMeeting("Other", DateTime.UtcNow);
        db.AddTag(b, "Planning");
        db.MoveToFolder(c, "Clients");
        Assert.Equal(new[] { "Weekly 2", "Weekly 1" }, db.ListMeetings(seriesId: "weekly").Select(m => m.Title));
        Assert.Equal("Other", Assert.Single(db.ListMeetings(folder: "Clients")).Title);
        Assert.Equal("Weekly 2", Assert.Single(db.ListMeetings(tag: "planning")).Title);
    }
}
