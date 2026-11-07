namespace MeetWise.Core;

public sealed record LanguageInfo(string Code, string Iso3, string English, string Portuguese, string Spanish)
{
    public string NameIn(string uiLanguage) => uiLanguage switch { "pt" => Portuguese, "es" => Spanish, _ => English };
}

/// <summary>
/// The 20 most spoken languages, as in the subtitle extractor: Whisper codes, ISO 639-2 codes used in media track tags, and names in the
/// three interface languages. "auto" means "detect the language from the audio".
/// </summary>
public static class Languages
{
    public const string Auto = "auto";

    public static readonly IReadOnlyList<LanguageInfo> All = new LanguageInfo[]
    {
        new("en", "eng", "English", "Inglês", "Inglés"),
        new("zh", "zho", "Mandarin Chinese", "Chinês mandarim", "Chino mandarín"),
        new("hi", "hin", "Hindi", "Hindi", "Hindi"),
        new("es", "spa", "Spanish", "Espanhol", "Español"),
        new("fr", "fra", "French", "Francês", "Francés"),
        new("ar", "ara", "Arabic", "Árabe", "Árabe"),
        new("bn", "ben", "Bengali", "Bengali", "Bengalí"),
        new("pt", "por", "Portuguese", "Português", "Portugués"),
        new("ru", "rus", "Russian", "Russo", "Ruso"),
        new("ur", "urd", "Urdu", "Urdu", "Urdu"),
        new("id", "ind", "Indonesian", "Indonésio", "Indonesio"),
        new("de", "deu", "German", "Alemão", "Alemán"),
        new("ja", "jpn", "Japanese", "Japonês", "Japonés"),
        new("pa", "pan", "Punjabi", "Punjabi", "Punyabí"),
        new("mr", "mar", "Marathi", "Marathi", "Maratí"),
        new("te", "tel", "Telugu", "Telugu", "Telugu"),
        new("tr", "tur", "Turkish", "Turco", "Turco"),
        new("ta", "tam", "Tamil", "Tâmil", "Tamil"),
        new("vi", "vie", "Vietnamese", "Vietnamita", "Vietnamita"),
        new("ko", "kor", "Korean", "Coreano", "Coreano")
    };

    /// <summary>Alternative ISO 639-2 spellings found in media files (bibliographic codes).</summary>
    private static readonly Dictionary<string, string[]> MetadataAliases = new()
    {
        ["zh"] = new[] { "chi" },
        ["fr"] = new[] { "fre" },
        ["de"] = new[] { "ger" }
    };

    /// <summary>Caption language variants YouTube uses.</summary>
    private static readonly Dictionary<string, string[]> YouTubeVariants = new()
    {
        ["zh"] = new[] { "zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-HK" },
        ["pt"] = new[] { "pt-BR", "pt-PT" },
        ["en"] = new[] { "en-US", "en-GB" },
        ["es"] = new[] { "es-419", "es-ES", "es-MX" },
        ["fr"] = new[] { "fr-FR", "fr-CA" },
        ["de"] = new[] { "de-DE" },
        ["ar"] = new[] { "ar-SA", "ar-EG" }
    };

    public static LanguageInfo? Find(string? code) => All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? code) => code == Auto || Find(code) is not null;

    /// <summary>Name for display, or the auto-detect label.</summary>
    public static string Name(string code, string uiLanguage)
        => code == Auto ? uiLanguage switch { "pt" => "Detectar automaticamente", "es" => "Detectar automáticamente", _ => "Detect automatically" }
                        : Find(code)?.NameIn(uiLanguage) ?? code;

    /// <summary>Every tag an audio track may carry for this language (lower case): the two-letter code, ISO 639-2 and its aliases.</summary>
    public static IReadOnlySet<string> TrackTags(string code)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { code };
        if (Find(code) is { } l) tags.Add(l.Iso3);
        if (MetadataAliases.TryGetValue(code.ToLowerInvariant(), out var extra)) foreach (var e in extra) tags.Add(e);
        return tags;
    }

    /// <summary>Caption codes to try on YouTube for a language, most specific first.</summary>
    public static IReadOnlyList<string> YouTubeCodes(string code)
        => YouTubeVariants.TryGetValue(code.ToLowerInvariant(), out var v) ? new[] { code }.Concat(v).ToList() : new[] { code };
}
