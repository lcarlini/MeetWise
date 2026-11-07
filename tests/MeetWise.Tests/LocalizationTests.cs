using System.Text.RegularExpressions;
using MeetWise.App;

namespace MeetWise.Tests;

public class LocalizationTests
{
    private static string AppSource => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MeetWise.App"));

    [Fact]
    public void EveryTextExists_InEnglishPortugueseAndSpanish()
    {
        var english = Loc.Texts["en"].Keys.ToHashSet();
        foreach (var lang in new[] { "pt", "es" })
        {
            Assert.Empty(english.Except(Loc.Texts[lang].Keys));          // nothing untranslated
            Assert.Empty(Loc.Texts[lang].Keys.Except(english));          // nothing orphaned
        }
        Assert.All(Loc.Texts, kv => Assert.All(kv.Value, t => Assert.False(string.IsNullOrWhiteSpace(t.Value), $"{kv.Key}/{t.Key} is empty")));
    }

    [Fact]
    public void PlaceholdersMatch_InEveryLanguage()
    {
        // "{0}" and "{1}" must appear in the translation exactly when they appear in the English text
        foreach (var key in Loc.Keys)
        {
            var expected = Regex.Matches(Loc.Texts["en"][key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).ToList();
            foreach (var lang in new[] { "pt", "es" })
                Assert.Equal(expected, Regex.Matches(Loc.Texts[lang][key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).ToList());
        }
    }

    [Fact]
    public void EveryKeyUsedInXamlOrCode_IsDefined()
    {
        if (!Directory.Exists(AppSource)) return;
        var used = new HashSet<string>();
        foreach (var file in Directory.GetFiles(AppSource, "*.xaml").Where(f => !f.EndsWith("Styles.xaml")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{local:T ([\w.]+)\}")) used.Add(m.Groups[1].Value);
        foreach (var file in Directory.GetFiles(AppSource, "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.T\(""([\w.]+)"""))
                used.Add(m.Groups[1].Value);
        Assert.NotEmpty(used);
        // keys built at run time ("lib.kind." + kind) are checked as families instead
        var families = used.Where(k => k.EndsWith('.')).ToList();
        used.ExceptWith(families);
        var missing = used.Where(k => !Loc.Texts["en"].ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "undefined texts: " + string.Join(", ", missing));
        foreach (var kind in new[] { "recording", "import", "youtube", "meeting" })
            Assert.True(Loc.Texts["en"].ContainsKey("lib.kind." + kind), "lib.kind." + kind);
        Assert.Equal(new[] { "lib.kind." }, families);
    }

    [Fact]
    public void ChangingTheLanguage_ChangesTheTexts_AndRaisesTheIndexerNotification()
    {
        var loc = Loc.Instance;
        var original = loc.Language;
        var raised = new List<string?>();
        loc.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        try
        {
            loc.Language = "pt";
            Assert.Equal("Iniciar gravação", loc["rec.start"]);
            loc.Language = "es";
            Assert.Equal("Iniciar grabación", loc["rec.start"]);
            loc.Language = "en";
            Assert.Equal("Start recording", loc["rec.start"]);
            Assert.Contains("Item[]", raised);               // every {local:T} binding refreshes on this
            loc.Language = "xx";                             // an unknown language falls back to English
            Assert.Equal("en", loc.Language);
            Assert.Equal("no.such.key", loc["no.such.key"]); // a missing key shows itself, never crashes
        }
        finally { loc.Language = original; }
    }

    [Fact]
    public void Formatting_InsertsTheArguments_InEachLanguage()
    {
        Assert.Equal("3 of 10 selected", Loc.Get("sub.selected", "en").Replace("{0}", "3").Replace("{1}", "10"));
        Assert.Equal("3 de 10 selecionadas", Loc.Get("sub.selected", "pt").Replace("{0}", "3").Replace("{1}", "10"));
        Assert.Equal("3 de 10 seleccionadas", Loc.Get("sub.selected", "es").Replace("{0}", "3").Replace("{1}", "10"));
    }
}
