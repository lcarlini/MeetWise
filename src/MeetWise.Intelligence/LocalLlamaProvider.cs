using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace MeetWise.Intelligence;

/// <summary>
/// Fully local LLM through llama.cpp (LLamaSharp). Loads a GGUF model once and serves requests one at a time.
/// The CUDA backend is used automatically when the app ships LLamaSharp.Backend.Cuda12 and an NVIDIA GPU is present;
/// otherwise the CPU backend runs.
/// </summary>
public sealed class LocalLlamaProvider : ILlmProvider, IDisposable
{
    private readonly ModelParams _params;
    private readonly LLamaWeights _weights;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _name;

    public string Name => _name;
    public int ContextTokens { get; }

    /// <param name="gpuLayers">Layers offloaded to the GPU; 0 = CPU only. Use 99 to offload everything that fits.</param>
    public LocalLlamaProvider(string ggufPath, int contextTokens = 4096, int gpuLayers = 0)
    {
        // The backend is chosen once per process: use the CUDA backend whenever the GPU pack is installed (gpuLayers = 0 still runs on the CPU).
        LlamaBackend.Configure(MeetWise.Core.Models.CudaRuntimePack.Activate());
        ContextTokens = contextTokens;
        _name = "local " + Path.GetFileNameWithoutExtension(ggufPath);
        _params = new ModelParams(ggufPath)
        {
            ContextSize = (uint)contextTokens,
            GpuLayerCount = gpuLayers
        };
        _weights = LLamaWeights.LoadFromFile(_params);
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, int maxTokens = 1024, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var template = new LLamaTemplate(_weights);
            template.Add("system", system + " /no_think");
            foreach (var m in messages) template.Add(m.Role, m.Content);
            template.AddAssistant = true;
            var prompt = Encoding.UTF8.GetString(template.Apply());

            var executor = new StatelessExecutor(_weights, _params);
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f }
            };
            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inference, ct))
                sb.Append(token);
            return StripThinking(sb.ToString());
        }
        finally { _gate.Release(); }
    }

    /// <summary>Qwen3 and similar reasoning models emit a think block first; callers only want the answer.</summary>
    public static string StripThinking(string text)
    {
        int end = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0) text = text[(end + "</think>".Length)..];
        return text.Trim();
    }

    public void Dispose() => _weights.Dispose();
}
