using MeetWise.Intelligence;

namespace MeetWise.Tests;

public class LocalLlmTests
{
    private static string Gguf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetWise", "models", "llm", "qwen2.5-0.5b-instruct-q4_k_m.gguf");

    [Fact]
    public async Task LocalLlama_GeneratesText_OnCpu()
    {
        if (!File.Exists(Gguf)) return;
        using var llm = new LocalLlamaProvider(Gguf, contextTokens: 2048);
        var reply = await llm.CompleteAsync("You are a calculator. Answer with just the number.",
            new[] { new ChatMessage("user", "What is 2 + 2?") }, 16);
        Assert.Contains("4", reply);
    }
}

public class LlmSelectionTests
{
    private static MeetWise.Core.Hardware.HardwareProfile Hw(long vram) => new(
        vram > 0 ? new[] { new MeetWise.Core.Hardware.GpuInfo("g", vram, true) } : Array.Empty<MeetWise.Core.Hardware.GpuInfo>(),
        16, MeetWise.Core.Settings.HardwareTier.A_NvidiaRtx, "x");

    [Fact] public void Rtx4070Mobile_GetsQwen8B_OnGpu() => Assert.Equal(("Qwen3-8B-Q4_K_M.gguf", 99), Pick(8188));
    [Fact] public void SmallGpu_Gets4B_OnGpu() => Assert.Equal(("Qwen3-4B-Q4_K_M.gguf", 99), Pick(4096));
    [Fact] public void NoGpu_Gets4B_OnCpu() => Assert.Equal(("Qwen3-4B-Q4_K_M.gguf", 0), Pick(0));

    private static (string, int) Pick(long vram)
    {
        var (m, l) = MeetWise.Core.Models.ModelCatalog.LlmFor(Hw(vram));
        return (m.Id, l);
    }

    [Fact]
    public void StripThinking_RemovesReasoningBlock()
        => Assert.Equal("answer", MeetWise.Intelligence.LocalLlamaProvider.StripThinking("<think>hmm\nmore</think>\n\nanswer"));
}
