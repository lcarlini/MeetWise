using System.Text.RegularExpressions;
using MeetWise.App;

namespace MeetWise.Tests;

public class LocalizationTests
{
    private static string AppSource => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MeetWise.App"));

    [Fact]
    public void EveryTextExists_InEnglishAndPortuguese()
    {
        var english = Loc.Texts["en"].Keys.ToHashSet();
        Assert.Empty(english.Except(Loc.Texts["pt"].Keys));
        Assert.Empty(Loc.Texts["pt"].Keys.Except(english));
        Assert.All(Loc.Texts, kv => Assert.All(kv.Value, t => Assert.False(string.IsNullOrWhiteSpace(t.Value), $"{kv.Key}/{t.Key} is empty")));
    }

    [Fact]
    public void PlaceholdersMatch_InEveryLanguage()
    {
        foreach (var key in Loc.Keys)
        {
            var expected = Regex.Matches(Loc.Texts["en"][key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).ToList();
            Assert.Equal(expected, Regex.Matches(Loc.Texts["pt"][key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).ToList());
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
        var missing = used.Where(k => !Loc.Texts["en"].ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "undefined texts: " + string.Join(", ", missing));
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
            Assert.Equal("Ligar", loc["mic.turnon"]);
            loc.Language = "en";
            Assert.Equal("Turn on", loc["mic.turnon"]);
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
        var loc = Loc.Instance;
        var original = loc.Language;
        try
        {
            loc.Language = "en";
            Assert.Equal("Fixed automatically: x.", Loc.T("state.fixed", "x"));
            loc.Language = "pt";
            Assert.Equal("Corrigido automaticamente: x.", Loc.T("state.fixed", "x"));
        }
        finally { loc.Language = original; }
    }
}
