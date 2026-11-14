using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace MeetWise.App;

/// <summary>
/// Interface texts in English and Brazilian Portuguese. XAML uses <c>{local:T key}</c>; changing <see cref="Language"/> updates every visible
/// text immediately. The texts themselves live in <see cref="LocTexts"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language
    {
        get => _language;
        set
        {
            var v = Texts.ContainsKey(value) ? value : "en";
            if (v == _language) return;
            _language = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // every binding to the indexer refreshes
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => Get(key, _language);

    public static string T(string key, params object[] args)
    {
        var text = Instance[key];
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public static string Get(string key, string language)
        => Texts.TryGetValue(language, out var d) && d.TryGetValue(key, out var v) ? v
         : Texts["en"].TryGetValue(key, out var en) ? en : key;

    public static IEnumerable<string> Keys => Texts["en"].Keys;
    public static IReadOnlyList<string> SupportedLanguages { get; } = new[] { "en", "pt" };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts = Build();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build()
    {
        var en = new Dictionary<string, string>();
        var pt = new Dictionary<string, string>();
        foreach (var (key, e, p, _) in LocTexts.Items) { en[key] = e; pt[key] = p; }
        return new Dictionary<string, IReadOnlyDictionary<string, string>> { ["en"] = en, ["pt"] = pt };
    }
}

/// <summary>XAML: <c>Text="{local:T mic.title}"</c>. Binds to <see cref="Loc"/> so a language change updates the text live.</summary>
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
