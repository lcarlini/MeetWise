using MeetWise.Intelligence;

namespace MeetWise.Tests;

/// <summary>Runs the real coach prompt against the real local model (Qwen3-8B on the GPU). Skipped when the model is not downloaded.</summary>
public class CoachEvalTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public CoachEvalTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

    private static string Gguf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "llm", "Qwen3-8B-Q4_K_M.gguf");

    private static readonly string[] WithMistakes =
    {
        "I goes to the office yesterday and meet my manager.",
        "She don't know the answer to this question.",
        "We was discussing the budget when he arrived.",
        "He have three years of experience in sales.",
        "Yesterday I am sending the report to the client.",
        "The team have finished the project last week.",
        "I am agree with your proposal about the timeline.",
        "Can you explain me how the pricing works?",
        "There is many problems with the current process.",
        "I didn't saw the email that you sent this morning.",
        "She suggested me to wait until Monday for the answer.",
        "We discussed about the new strategy in the meeting.",
        "I will call you when I will arrive at the office.",
        "He said me that the deadline was moved to Friday.",
        "My colleague are working on the same task right now."
    };

    private static readonly string[] Correct =
    {
        "We should schedule the meeting for Thursday.",
        "I went to the office yesterday and met my manager.",
        "Could you share the numbers from last quarter before we start?",
        "So, the main risk is the delivery date, and we need a plan.",
        "She doesn't know the answer to that question yet.",
        "I think we can ship the first version next week.",
        "Thanks everyone, let's summarize the decisions and next steps.",
        "He has three years of experience in sales.",
        "We were discussing the budget when he arrived.",
        "I'm going to send the report to the client tomorrow morning.",
        "Let me know if you have any questions about the pricing.",
        "The team finished the project last week.",
        "I agree with your proposal about the timeline.",
        "And that is why we decided to move the deadline.",
        "Okay, so I'll call you when I get to the office."
    };

    [Fact]
    public async Task Coach_CatchesRealMistakes_WithoutInventingCorrections()
    {
        if (!File.Exists(Gguf)) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        using var llm = new LocalLlamaProvider(Gguf, contextTokens: 4096, gpuLayers: 99);
        var coach = new SpeechCoach(llm);

        int caught = 0, falseAlarms = 0;
        foreach (var s in WithMistakes)
        {
            var fb = await coach.ReviewAsync(s);
            if (!fb.Ok) caught++;
            _out.WriteLine($"[{(fb.Ok ? "MISSED" : "caught")}] {s}  =>  {fb.Corrected}");
        }
        foreach (var s in Correct)
        {
            var fb = await coach.ReviewAsync(s);
            if (!fb.Ok) { falseAlarms++; _out.WriteLine($"[FALSE ALARM] {s}  =>  {fb.Corrected}  ({string.Join("; ", fb.Issues.Select(i => i.Original + " -> " + i.Suggestion))})"); }
        }
        _out.WriteLine($"caught {caught}/{WithMistakes.Length}, false alarms {falseAlarms}/{Correct.Length}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "coach-eval.txt"), $"caught {caught}/{WithMistakes.Length}, false alarms {falseAlarms}/{Correct.Length}");

        Assert.True(caught >= 10, $"only {caught} of {WithMistakes.Length} mistakes caught");
        Assert.True(falseAlarms <= 1, $"{falseAlarms} correct sentences were wrongly corrected");
    }
}
