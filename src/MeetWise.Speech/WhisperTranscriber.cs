using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MeetWise.Speech;

public sealed record WhisperSegment(double Start, double End, string Text, string? Language);

/// <summary>
/// High-accuracy second pass and multilingual transcription with Whisper (whisper.cpp). Uses CUDA on NVIDIA GPUs when the
/// runtime and driver are present and falls back to the CPU automatically.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;

    static WhisperTranscriber()
    {
        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu };
    }

    public WhisperTranscriber(string ggmlModelPath) => _factory = WhisperFactory.FromPath(ggmlModelPath);

    /// <param name="samples16k">Mono 16 kHz float audio.</param>
    /// <param name="language">ISO code such as "en", "pt", "es", "hi", "ko", or "auto" to detect.</param>
    /// <param name="vocabulary">Custom terms (names, acronyms) given to the model as a prompt so it spells them correctly.</param>
    public async Task<IReadOnlyList<WhisperSegment>> TranscribeAsync(float[] samples16k, string language = "auto",
        IEnumerable<string>? vocabulary = null, int threads = 4, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var builder = _factory.CreateBuilder().WithLanguage(language).WithThreads(threads);
        var terms = vocabulary?.Where(v => v.Length > 0).Take(120).ToList();
        if (terms is { Count: > 0 }) builder = builder.WithPrompt("Vocabulary: " + string.Join(", ", terms) + ".");
        await using var processor = builder.Build();

        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(16000, 16, 1)) { })
        {
            writer.WriteSamples(samples16k, 0, samples16k.Length);
            writer.Flush();
            wav.Position = 0;
            var copy = new MemoryStream(wav.ToArray());
            var result = new List<WhisperSegment>();
            double total = samples16k.Length / 16000.0;
            await foreach (var seg in processor.ProcessAsync(copy, ct))
            {
                var text = seg.Text.Trim();
                if (text.Length == 0) continue;
                result.Add(new WhisperSegment(seg.Start.TotalSeconds, seg.End.TotalSeconds, text, seg.Language));
                if (total > 0) progress?.Report(Math.Min(1, seg.End.TotalSeconds / total));
            }
            return result;
        }
    }

    public void Dispose() => _factory.Dispose();
}
