using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetWise.Storage;

namespace MeetWise.Intelligence;

public sealed record MeetingNotes(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<string> Decisions,
    IReadOnlyList<NoteActionItem> ActionItems, IReadOnlyList<string> Risks);

public sealed record NoteActionItem(string Text, string? Owner, DateTime? DueUtc);

public sealed record MeetingTemplate(string Id, string Name, string Focus);

public static class MeetingTemplates
{
    public static readonly IReadOnlyList<MeetingTemplate> All = new[]
    {
        new MeetingTemplate("general", "General meeting", "Capture the main topics, decisions, and who owes what by when."),
        new MeetingTemplate("standup", "Engineering standup", "For each person: done since last time, planned next, blockers. Highlight blockers as risks."),
        new MeetingTemplate("client", "Client call", "Client requirements, commitments we made, open questions, budget or timeline statements, next steps."),
        new MeetingTemplate("interview", "Interview", "Candidate background, strengths, concerns, evidence for each competency, and a hire/no-hire signal if one was expressed."),
        new MeetingTemplate("sales", "Sales meeting", "Customer pain points, objections, buying signals, decision makers, pricing discussion, next steps."),
        new MeetingTemplate("incident", "Incident review", "Timeline, root cause, impact, mitigations, owners and deadlines for follow-ups, unresolved risks."),
        new MeetingTemplate("one-on-one", "1:1", "Feedback given, goals and growth topics, concerns, commitments from both sides.")
    };

    public static MeetingTemplate Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}

/// <summary>Personalisation injected into prompts so notes and answers match the user's role.</summary>
public sealed record UserProfile(string? Nickname = null, string? Occupation = null, string? Context = null);

/// <summary>Notes, summaries and action items from a transcript. Long transcripts are summarised in chunks, then merged.</summary>
public sealed class MeetingIntelligence
{
    private readonly ILlmProvider _llm;

    public MeetingIntelligence(ILlmProvider llm) => _llm = llm;

    public static string FormatTranscript(IEnumerable<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
            sb.Append('[').Append(TimeSpan.FromSeconds(s.StartSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(s.Speaker).Append(": ").AppendLine(s.Text);
        return sb.ToString();
    }

    /// <summary>Roughly 4 characters per token; leave half the window for the prompt and the answer.</summary>
    public static IReadOnlyList<string> Chunk(string transcript, int contextTokens)
    {
        int maxChars = Math.Max(2000, contextTokens * 4 / 2);
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in transcript.Split('\n'))
        {
            if (current.Length + line.Length > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.AppendLine(line.TrimEnd('\r'));
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static string SystemPrompt(MeetingTemplate t, UserProfile? p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You turn meeting transcripts into structured notes. Use only facts stated in the transcript; never invent owners, dates or decisions.");
        sb.AppendLine($"Meeting type: {t.Name}. Focus: {t.Focus}");
        if (p is not null && (p.Occupation is not null || p.Context is not null))
            sb.AppendLine($"The reader is {p.Nickname ?? "the user"}, {p.Occupation ?? "a professional"}. {p.Context} Write so it is useful to them.");
        sb.AppendLine("Respond with ONLY a JSON object: {\"summary\": string, \"key_points\": [string], \"decisions\": [string], \"risks\": [string], \"action_items\": [{\"text\": string, \"owner\": string|null, \"due\": \"YYYY-MM-DD\"|null}]}.");
        sb.AppendLine("Write in the language of the transcript.");
        return sb.ToString();
    }

    public async Task<MeetingNotes> GenerateNotesAsync(string transcript, string? templateId = null, UserProfile? profile = null,
        DateTime? meetingDate = null, CancellationToken ct = default)
    {
        var template = MeetingTemplates.Get(templateId);
        var system = SystemPrompt(template, profile);
        var date = (meetingDate ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var chunks = Chunk(transcript, _llm.ContextTokens);

        var partials = new List<MeetingNotes>();
        foreach (var chunk in chunks)
        {
            var prompt = $"Meeting date: {date} (resolve relative dates like \"Friday\" against it).\n\nTRANSCRIPT:\n{chunk}";
            var reply = await _llm.CompleteAsync(system, new[] { new ChatMessage("user", prompt) }, 1500, ct);
            partials.Add(ParseNotes(reply));
        }
        if (partials.Count == 1) return partials[0];

        var merged = string.Join("\n\n", partials.Select((p, i) => $"PART {i + 1}:\n{Serialize(p)}"));
        var mergePrompt = "Merge these partial notes of one meeting into a single set of notes. Remove duplicates, keep every distinct action item.\n\n" + merged;
        var final = await _llm.CompleteAsync(system, new[] { new ChatMessage("user", mergePrompt) }, 1500, ct);
        return ParseNotes(final);
    }

    public static string Serialize(MeetingNotes n) => new JsonObject
    {
        ["summary"] = n.Summary,
        ["key_points"] = new JsonArray(n.KeyPoints.Select(x => (JsonNode)x).ToArray()),
        ["decisions"] = new JsonArray(n.Decisions.Select(x => (JsonNode)x).ToArray()),
        ["risks"] = new JsonArray(n.Risks.Select(x => (JsonNode)x).ToArray()),
        ["action_items"] = new JsonArray(n.ActionItems.Select(a => (JsonNode)new JsonObject
        {
            ["text"] = a.Text,
            ["owner"] = a.Owner,
            ["due"] = a.DueUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }).ToArray())
    }.ToJsonString();

    /// <summary>Models often wrap JSON in prose or code fences; extract the first balanced object.</summary>
    public static MeetingNotes ParseNotes(string reply)
    {
        var json = ExtractJsonObject(reply) ?? throw new FormatException("The model did not return JSON notes.");
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })!.AsObject();
        static IReadOnlyList<string> Strings(JsonNode? n) =>
            n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : Array.Empty<string>();

        var actions = new List<NoteActionItem>();
        if (root["action_items"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var text = item?["text"]?.ToString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var owner = item?["owner"]?.ToString();
                DateTime? due = DateTime.TryParseExact(item?["due"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
                actions.Add(new NoteActionItem(text!, string.IsNullOrWhiteSpace(owner) || owner == "null" ? null : owner, due));
            }
        }
        return new MeetingNotes(root["summary"]?.ToString() ?? "", Strings(root["key_points"]), Strings(root["decisions"]), actions, Strings(root["risks"]));
    }

    public static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var candidate = text.Substring(start, i - start + 1);
                    try { JsonNode.Parse(candidate); return candidate; } catch (JsonException) { break; }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    /// <summary>Saves generated notes: the summary on the meeting and each action item in the central list.</summary>
    public static void Save(MeetingDatabase db, long meetingId, MeetingNotes notes)
    {
        db.SetSummary(meetingId, Serialize(notes));
        foreach (var a in notes.ActionItems) db.AddActionItem(meetingId, a.Text, a.Owner, a.DueUtc);
    }
}
