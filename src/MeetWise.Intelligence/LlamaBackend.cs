using LLama.Abstractions;
using LLama.Native;
using MeetWise.Core.Logging;

namespace MeetWise.Intelligence;

/// <summary>
/// Chooses the llama.cpp native backend. LLamaSharp normally detects CUDA from a full CUDA Toolkit install, which most users do not
/// have, so this policy prefers the bundled CUDA 13 backend whenever MeetWise's GPU acceleration pack is active, and falls back to the CPU.
/// Must run before the first model is loaded in the process.
/// </summary>
public static class LlamaBackend
{
    private static bool _configured;
    private static readonly object Gate = new();

    public static void Configure(bool useGpu)
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;
            NativeLibraryConfig.All.WithSelectingPolicy(new PackAwarePolicy(useGpu)).WithVulkan(false).WithAutoFallback(true);
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (message.Contains("Failed", StringComparison.OrdinalIgnoreCase) || message.Contains("Successfully loaded './runtimes/win-x64/native/cuda", StringComparison.OrdinalIgnoreCase))
                    AppLog.Info("llama.cpp: " + message.Trim());
            });
        }
    }

    private sealed class PackAwarePolicy : INativeLibrarySelectingPolicy
    {
        private readonly bool _gpu;
        public PackAwarePolicy(bool gpu) => _gpu = gpu;

        public IEnumerable<INativeLibrary> Apply(NativeLibraryConfig.Description description, SystemInfo systemInfo, NativeLogConfig.LLamaLogCallback? logCallback)
        {
            if (_gpu)
                yield return new NativeLibraryWithCuda(13, description.Library, description.AvxLevel, description.SkipCheck);
            yield return new NativeLibraryWithAvx(description.Library, description.AvxLevel, description.SkipCheck);
            yield return new NativeLibraryWithAvx(description.Library, AvxLevel.None, description.SkipCheck);
        }
    }
}
