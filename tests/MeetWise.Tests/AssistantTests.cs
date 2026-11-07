using MeetWise.Intelligence;

namespace MeetWise.Tests;

public class AssistantTests
{
    private static readonly AssistantLine[] Meeting =
    {
        new(5, "Me", "We need to finalize the budget by Friday."),
        new(12, "Others", "Ana will update the Kubernetes manifests before the release."),
        new(31, "Me", "Let's move the launch to the fifteenth of March."),
        new(3725, "Others", "I will send the customer the revised contract.")
    };

    [Fact]
    public void FormatLines_UsesTimestampsAndSpeakers()
        => Assert.Equal("[00:00:05] Me: We need to finalize the budget by Friday.\n[01:02:05] Others: I will send the customer the revised contract.\n".Replace("\n", Environment.NewLine),
            TranscriptAssistant.FormatLines(new[] { Meeting[0], Meeting[3] }));

    [Theory]
    [InlineData(AssistantCommand.Summarize, "Summarize")]
    [InlineData(AssistantCommand.KeyPoints, "key points")]
    [InlineData(AssistantCommand.ActionItems, "action item")]
    [InlineData(AssistantCommand.Chapters, "timestamp")]
    [InlineData(AssistantCommand.TitleAndDescription, "title")]
    [InlineData(AssistantCommand.FollowUpEmail, "email")]
    [InlineData(AssistantCommand.CleanUp, "punctuation")]
    public void EveryCommand_HasAClearInstruction(AssistantCommand command, string keyword)
        => Assert.Contains(keyword, TranscriptAssistant.Instruction(new AssistantRequest(command)), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Translate_NamesTheTargetLanguage_AndCustomPassesTheUsersWordsThrough()
    {
        Assert.Contains("Portuguese", TranscriptAssistant.Instruction(new AssistantRequest(AssistantCommand.Translate, Language: "pt")));
        Assert.Equal("Who owns the contract?", TranscriptAssistant.Instruction(new AssistantRequest(AssistantCommand.Custom, "  Who owns the contract?  ")));
        Assert.Contains("Summarize", TranscriptAssistant.Instruction(new AssistantRequest(AssistantCommand.Custom, "   ")));
    }

    [Fact]
    public async Task WholeRecording_GoesToTheModel_WithRulesAgainstInventing()
    {
        var llm = new FakeLlm { Reply = _ => "  A summary.  " };
        var answer = await new TranscriptAssistant(llm).RunAsync(Meeting, new AssistantRequest(AssistantCommand.Summarize));
        Assert.Equal("A summary.", answer);
        var call = Assert.Single(llm.Prompts);
        Assert.Contains("whole transcript", call.System);
        Assert.Contains("never invent", call.System);
        Assert.Contains("[00:00:12] Others: Ana will update the Kubernetes manifests", call.System);
        Assert.Contains("[01:02:05]", call.System);
        Assert.StartsWith("Summarize", call.User);
    }

    [Fact]
    public async Task Selection_IsTheOnlyContext_AndTheModelIsToldSo()
    {
        var llm = new FakeLlm();
        await new TranscriptAssistant(llm).RunAsync(new[] { Meeting[1] }, new AssistantRequest(AssistantCommand.ActionItems), isSelection: true);
        var call = Assert.Single(llm.Prompts);
        Assert.Contains("selected specific lines", call.System);
        Assert.Contains("Kubernetes", call.System);
        Assert.DoesNotContain("budget", call.System);
        Assert.DoesNotContain("contract", call.System);
    }

    [Fact]
    public async Task FollowUps_KeepTheConversation_InOrder_BeforeTheNewCommand()
    {
        var seen = new List<IReadOnlyList<ChatMessage>>();
        var llm = new CapturingLlm(seen);
        var history = new[] { new ChatMessage("user", "Summarize"), new ChatMessage("assistant", "It was about the budget.") };
        await new TranscriptAssistant(llm).RunAsync(Meeting, new AssistantRequest(AssistantCommand.Custom, "Make it shorter"), history: history);
        Assert.Equal(new[] { "Summarize", "It was about the budget.", "Make it shorter" }, seen[0].Select(m => m.Content));
        Assert.Equal(new[] { "user", "assistant", "user" }, seen[0].Select(m => m.Role));
    }

    private sealed class CapturingLlm : ILlmProvider
    {
        private readonly List<IReadOnlyList<ChatMessage>> _seen;
        public CapturingLlm(List<IReadOnlyList<ChatMessage>> seen) => _seen = seen;
        public string Name => "capture";
        public int ContextTokens => 8000;
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
        {
            _seen.Add(messages.ToList());
            return Task.FromResult("ok");
        }
    }

    private static AssistantLine[] LongMeeting(int lines)
        => Enumerable.Range(0, lines).Select(i => new AssistantLine(i * 4, i % 2 == 0 ? "Me" : "Others", $"Sentence number {i} about the roadmap and the release plan for this quarter.")).ToArray();

    [Fact]
    public async Task LongRecording_IsCondensedPieceByPiece_ThenAnsweredFromTheNotes()
    {
        var llm = new FakeLlm { ContextTokens = 1000, Reply = u => u.StartsWith("Condense") ? "NOTE: roadmap discussed." : "Final answer." };
        var answer = await new TranscriptAssistant(llm).RunAsync(LongMeeting(300), new AssistantRequest(AssistantCommand.Summarize));

        Assert.Equal("Final answer.", answer);
        var maps = llm.Prompts.Where(p => p.User.StartsWith("Condense")).ToList();
        Assert.True(maps.Count >= 3, $"expected several map calls, got {maps.Count}");
        Assert.All(maps, m => Assert.True(m.System.Length < 1000 * 4, "each piece must fit the model window"));
        var final = llm.Prompts[^1];
        Assert.StartsWith("Summarize", final.User);
        Assert.Contains("condensed notes", final.System);
        Assert.Contains("NOTE: roadmap discussed.", final.System);
        Assert.DoesNotContain("Sentence number 150", final.System); // the raw transcript is no longer sent
    }

    [Fact]
    public async Task LongCleanUp_RunsOnEachPiece_AndJoinsThem_InsteadOfSummarising()
    {
        int n = 0;
        var llm = new FakeLlm { ContextTokens = 1000, Reply = _ => $"cleaned part {++n}" };
        var answer = await new TranscriptAssistant(llm).RunAsync(LongMeeting(300), new AssistantRequest(AssistantCommand.CleanUp));
        Assert.Equal(llm.Prompts.Count, n);
        Assert.True(n >= 3);
        Assert.All(llm.Prompts, p => Assert.StartsWith("Rewrite the transcript", p.User));
        Assert.Equal(string.Join("\n\n", Enumerable.Range(1, n).Select(i => $"cleaned part {i}")), answer);
    }

    [Fact]
    public async Task EmptyTranscript_DoesNotCallTheModel()
    {
        var llm = new FakeLlm();
        var answer = await new TranscriptAssistant(llm).RunAsync(Array.Empty<AssistantLine>(), new AssistantRequest(AssistantCommand.Summarize));
        Assert.Contains("no transcript", answer);
        Assert.Empty(llm.Prompts);
    }

    [Fact]
    public async Task RealLocalModel_SummarisesAndFindsActionItems_AndTranslates()
    {
        var gguf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models", "llm", "Qwen3-8B-Q4_K_M.gguf");
        if (!File.Exists(gguf)) return;
        MeetWise.Core.Models.CudaRuntimePack.Activate();
        using var llm = new LocalLlamaProvider(gguf, contextTokens: 4096, gpuLayers: 99);
        var assistant = new TranscriptAssistant(llm);

        var summary = await assistant.RunAsync(Meeting, new AssistantRequest(AssistantCommand.Summarize));
        Assert.Contains("budget", summary, StringComparison.OrdinalIgnoreCase);

        var actions = await assistant.RunAsync(Meeting, new AssistantRequest(AssistantCommand.ActionItems));
        Assert.Contains("Ana", actions);
        Assert.Contains("manifests", actions, StringComparison.OrdinalIgnoreCase);

        var chapters = await assistant.RunAsync(Meeting, new AssistantRequest(AssistantCommand.Chapters));
        Assert.Contains("00:", chapters);

        var pt = await assistant.RunAsync(new[] { Meeting[0] }, new AssistantRequest(AssistantCommand.Translate, Language: "pt"), isSelection: true);
        Assert.Contains("orçamento", pt, StringComparison.OrdinalIgnoreCase);
    }
}
