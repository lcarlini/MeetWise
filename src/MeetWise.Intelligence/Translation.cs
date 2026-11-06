using System.Collections.Concurrent;

namespace MeetWise.Intelligence;

public interface ITranslator
{
    Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default);
}

/// <summary>Translation through whichever LLM is configured (local Qwen3 by default, which handles dozens of languages). Short and cached so live captions stay responsive.</summary>
public sealed class LlmTranslator : ITranslator
{
    private readonly ILlmProvider _llm;
    private readonly ConcurrentDictionary<(string, string), string> _cache = new();

    public LlmTranslator(ILlmProvider llm) => _llm = llm;

    public static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English", "pt" => "Portuguese", "es" => "Spanish", "fr" => "French", "de" => "German", "it" => "Italian",
        "nl" => "Dutch", "pl" => "Polish", "hi" => "Hindi", "ko" => "Korean", "ja" => "Japanese", "zh" => "Chinese",
        "ru" => "Russian", "tr" => "Turkish", "ar" => "Arabic", _ => code
    };

    public async Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        if (_cache.TryGetValue((targetLanguage, text), out var hit)) return hit;

        var from = sourceLanguage is null or "auto" ? "" : $" from {LanguageName(sourceLanguage)}";
        var system = $"You are a live interpreter. Translate the user's message{from} into {LanguageName(targetLanguage)}. "
            + "Output ONLY the translation: no quotes, notes or explanations. Keep names, numbers and technical terms intact. "
            + "If the message is already in the target language, repeat it unchanged.";
        var result = (await _llm.CompleteAsync(system, new[] { new ChatMessage("user", text) }, Math.Max(64, text.Length * 2), ct)).Trim().Trim('"');
        if (_cache.Count > 500) _cache.Clear();
        _cache[(targetLanguage, text)] = result;
        return result;
    }
}
