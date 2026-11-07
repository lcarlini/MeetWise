using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetWise.Intelligence;

public sealed record ChatMessage(string Role, string Content);

/// <summary>A text-generation backend. Implementations: Anthropic, OpenAI-compatible (also Ollama / LM Studio), local llama.cpp.</summary>
public interface ILlmProvider
{
    string Name { get; }

    /// <summary>Approximate context window in tokens, used to decide when to split a transcript.</summary>
    int ContextTokens { get; }

    Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default);
}

/// <summary>Anthropic Messages API. The API key is optional and supplied by the user; nothing is sent without it.</summary>
public sealed class AnthropicProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;

    public string Name => $"Anthropic {_model}";
    public int ContextTokens => 150_000;

    public AnthropicProvider(HttpClient http, string apiKey, string model = "claude-sonnet-5-5", string baseUrl = "https://api.anthropic.com")
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray())
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic API {(int)resp.StatusCode}: {Truncate(json)}", null, resp.StatusCode);

        var sb = new StringBuilder();
        foreach (var block in JsonNode.Parse(json)!["content"]!.AsArray())
            if (block?["type"]?.GetValue<string>() == "text") sb.Append(block["text"]!.GetValue<string>());
        return sb.ToString();
    }

    internal static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>OpenAI chat-completions compatible endpoint: OpenAI, Azure-style gateways, Ollama (http://localhost:11434/v1), LM Studio.</summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _model;

    public string Name => $"{_model} @ {_baseUrl}";
    public int ContextTokens { get; }

    public OpenAiCompatibleProvider(HttpClient http, string baseUrl, string model, string? apiKey = null, int contextTokens = 8192)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        ContextTokens = contextTokens;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages) msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        var body = new JsonObject { ["model"] = _model, ["max_tokens"] = maxTokens, ["messages"] = msgs, ["temperature"] = 0.2 };
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"LLM API {(int)resp.StatusCode}: {AnthropicProvider.Truncate(json)}", null, resp.StatusCode);
        return JsonNode.Parse(json)!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
    }
}
