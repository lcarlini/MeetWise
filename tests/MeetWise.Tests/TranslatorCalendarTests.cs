using MeetWise.Intelligence;

namespace MeetWise.Tests;

/// <summary>
/// A translation that turns Friday into Thursday reads perfectly and sends someone to the wrong day (the local 8B model really did this).
/// Days and months translate one to one, so they are checked exactly: ask again, and as a last resort put the right name in.
/// </summary>
public class TranslatorCalendarTests
{
    private static LlmTranslator Make(ScriptedLlm llm)
        => new(llm, new TranslatorOptions(Timeout: TimeSpan.FromSeconds(2)), null, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task AWrongWeekday_IsRetried_AndAFaithfulSecondAnswerIsUsed()
    {
        var llm = new ScriptedLlm { Handler = (n, _, _, _) => Task.FromResult(n == 1 ? "Podemos mover a reunião para quinta-feira?" : "Podemos mover a reunião para sexta-feira?") };
        var r = await Make(llm).TranslateDetailedAsync("Can we move the meeting to Friday?", "pt", "en");
        Assert.Equal("Podemos mover a reunião para sexta-feira?", r.Text);
        Assert.Equal(2, llm.CallCount);
        Assert.DoesNotContain("corrected", string.Join(";", r.Warnings));
    }

    [Fact]
    public async Task AModelThatInsistsOnTheWrongWeekday_IsCorrected_AndTheTextSaysSo()
    {
        var llm = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult("Podemos mover a reunião para quinta-feira?") };
        var r = await Make(llm).TranslateDetailedAsync("Can we move the meeting to Friday?", "pt", "en");
        Assert.Equal("Podemos mover a reunião para sexta-feira?", r.Text);
        Assert.Contains("weekday or month name corrected", r.Warnings);
    }

    [Theory]
    [InlineData("See you on Monday or Friday.", "Vejo você na segunda ou na quinta.", "Vejo você na segunda ou na sexta.", "pt")]
    [InlineData("The deadline is 14 March.", "O prazo é 14 de abril.", "O prazo é 14 de março.", "pt")]
    [InlineData("La reunión es el viernes.", "The meeting is on Thursday.", "The meeting is on Friday.", "en")]
    [InlineData("Wir treffen uns am Montag.", "Nos vemos el martes.", "Nos vemos el lunes.", "es")]
    [InlineData("Reunião na Sexta.", "Meeting on Thursday.", "Meeting on Friday.", "en")]
    public async Task WeekdaysAndMonths_AreCheckedInEveryDirection(string source, string wrong, string expected, string target)
    {
        var llm = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult(wrong) };
        var r = await Make(llm).TranslateDetailedAsync(source, target);
        Assert.Equal(expected, r.Text);
    }

    [Fact]
    public async Task CorrectNames_ArePassedThroughUntouched_AndWordsThatLookLikeNamesAreNotAProblem()
    {
        var ok = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult("A reunião é na sexta-feira, 14 de março.") };
        var r = await Make(ok).TranslateDetailedAsync("The meeting is on Friday, 14 March.", "pt", "en");
        Assert.Equal("A reunião é na sexta-feira, 14 de março.", r.Text);
        Assert.Equal(1, ok.CallCount);
        Assert.Empty(r.Warnings);

        // "may" is a verb here, "mar" is the sea: neither is a date, and nothing in the answer contradicts anything
        var verb = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult("Nós podemos enviar até o mar.") };
        var v = await Make(verb).TranslateDetailedAsync("We may ship by the sea.", "pt", "en");
        Assert.Equal(1, verb.CallCount);
        Assert.DoesNotContain("corrected", string.Join(";", v.Warnings));
    }

    [Theory]
    [InlineData("The call is at 3 PM.", "A ligação é às 15h.")]
    [InlineData("The call is at 3 PM.", "A ligação é às 3 da tarde.")]
    [InlineData("Lunch at 12 PM.", "Almoço às 12h.")]
    [InlineData("Meet at 5:30 pm.", "Encontro às 17:30.")]
    public async Task ATimeWrittenInTheOtherClockStyle_IsNotALostNumber(string source, string answer)
    {
        var llm = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult(answer) };
        var r = await Make(llm).TranslateDetailedAsync(source, "pt", "en");
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task ARealLostNumber_IsStillFlagged()
    {
        var llm = new ScriptedLlm { Handler = (_, _, _, _) => Task.FromResult("A ligação é às 4 da tarde.") };
        var r = await Make(llm).TranslateDetailedAsync("The call is at 3 PM.", "pt", "en");
        Assert.Contains("numbers differ from the original", r.Warnings);
    }
}
