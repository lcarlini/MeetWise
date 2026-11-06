using System.Security.Cryptography;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetWise.Storage;

namespace MeetWise.Intelligence;

public sealed record Answer(string Text, IReadOnlyList<TranscriptSegment> Sources);

/// <summary>"Ask MeetWise": answers questions about one meeting or all meetings, grounded in retrieved transcript passages with citations.</summary>
public sealed class AskMeetWise
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","of","to","in","on","for","with","is","are","was","were","be","what","who","when","where","why","how",
        "did","do","does","about","any","all","every","list","tell","me","us","that","this","it","we","they","i","you","from","by","at"
    };

    private readonly ILlmProvider _llm;
    private readonly MeetingDatabase _db;

    public AskMeetWise(ILlmProvider llm, MeetingDatabase db)
    {
        _llm = llm;
        _db = db;
    }

    public static IReadOnlyList<string> Keywords(string question)
        => Regex.Matches(question.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}'-]*")
            .Select(m => m.Value).Where(w => w.Length > 2 && !Stop.Contains(w)).Distinct().ToList();

    public async Task<Answer> AskAsync(string question, long? meetingId = null, UserProfile? profile = null, CancellationToken ct = default)
    {
        var hits = _db.SearchAny(Keywords(question), meetingId, 14);
        // Pull neighbouring lines so a hit is understood in context.
        var context = new List<TranscriptSegment>();
        foreach (var h in hits.OrderBy(h => h.MeetingId).ThenBy(h => h.StartSec))
            if (context.All(c => c.Id != h.Id)) context.Add(h);

        if (context.Count == 0)
            return new Answer("I could not find anything about that in the transcripts.", context);

        var sb = new StringBuilder();
        foreach (var s in context)
            sb.AppendLine($"[meeting {s.MeetingId} @ {TimeSpan.FromSeconds(s.StartSec).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}] {s.Speaker}: {s.Text}");

        var system = "You answer questions about meetings using ONLY the transcript excerpts provided. "
            + "Cite sources as [meeting N @ hh:mm:ss]. If the excerpts do not contain the answer, say so plainly. "
            + (profile?.Occupation is { } occ ? $"The user is {profile.Nickname ?? "a user"}, {occ}. " : "");
        var reply = await _llm.CompleteAsync(system, new[] { new ChatMessage("user", $"EXCERPTS:\n{sb}\nQUESTION: {question}") }, 800, ct);
        return new Answer(reply.Trim(), context);
    }
}

/// <summary>Custom vocabulary (names, acronyms, product terms). Corrects near-miss spellings the recognizer produces.</summary>
public sealed class Glossary
{
    private readonly List<string> _terms;
    public IReadOnlyList<string> Terms => _terms;

    public Glossary(IEnumerable<string> terms) => _terms = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Starter kits so a new workspace gets useful vocabulary immediately.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> StarterKits = new Dictionary<string, string[]>
    {
        ["software"] = new[] { "Kubernetes", "PostgreSQL", "GraphQL", "OAuth", "CI/CD", "Terraform", "microservice", "refactor", "latency", "regression" },
        ["sales"] = new[] { "ARR", "MRR", "pipeline", "churn", "upsell", "RFP", "SLA", "onboarding", "procurement", "discovery call" },
        ["medical"] = new[] { "hypertension", "myocardial", "anticoagulant", "contraindication", "prognosis", "differential diagnosis" },
        ["legal"] = new[] { "indemnification", "arbitration", "liability", "jurisdiction", "NDA", "force majeure", "statute of limitations" }
    };

    public string Apply(string text)
    {
        if (_terms.Count == 0 || string.IsNullOrWhiteSpace(text)) return text;
        var words = Regex.Matches(text, @"[\p{L}\p{N}'-]+").ToList();
        var sb = new StringBuilder(text);
        // Replace from the end so earlier indices stay valid.
        foreach (var m in Enumerable.Reverse(words))
        {
            var best = Best(m.Value);
            if (best is not null && !string.Equals(best, m.Value, StringComparison.Ordinal))
                sb.Remove(m.Index, m.Length).Insert(m.Index, best);
        }
        return sb.ToString();
    }

    private string? Best(string word)
    {
        if (word.Length < 4) return _terms.FirstOrDefault(t => t.Equals(word, StringComparison.OrdinalIgnoreCase));
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var t in _terms)
        {
            if (t.Contains(' ')) continue;
            int d = Levenshtein(word.ToLowerInvariant(), t.ToLowerInvariant());
            int limit = Math.Max(1, t.Length / 5);
            if (d <= limit && d < bestDist) { best = t; bestDist = d; }
        }
        return best;
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>API keys encrypted with Windows DPAPI (current user). Keys never go into settings.json.</summary>
public sealed class SecretStore
{
    private readonly string _dir;

    public SecretStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(MeetWise.Core.AppPaths.Roaming, "secrets");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string name) => Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16] + ".bin");

    public void Set(string name, string secret)
        => File.WriteAllBytes(PathFor(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("MeetWise:" + name), DataProtectionScope.CurrentUser));

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes("MeetWise:" + name), DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Remove(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Pushes action items to a user-configured webhook (Zapier, n8n, Jira/Linear automation, Slack workflow).</summary>
public sealed class WebhookExporter
{
    private readonly HttpClient _http;
    public WebhookExporter(HttpClient http) => _http = http;

    public async Task ExportAsync(string url, Meeting meeting, IEnumerable<ActionItem> items, CancellationToken ct = default)
    {
        var payload = new
        {
            meeting = new { id = meeting.Id, title = meeting.Title, startedUtc = meeting.StartedUtc },
            actionItems = items.Select(i => new { i.Id, i.Text, i.Owner, dueUtc = i.DueUtc, i.Done })
        };
        using var resp = await _http.PostAsJsonAsync(url, payload, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        resp.EnsureSuccessStatusCode();
    }
}
