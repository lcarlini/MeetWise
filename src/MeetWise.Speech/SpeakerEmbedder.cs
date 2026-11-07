using SherpaOnnx;

namespace MeetWise.Speech;

/// <summary>Turns speech into a fixed-size "voiceprint" (CAM++). Cosine similarity of two prints says how likely they are the same person.</summary>
public sealed class SpeakerEmbedder : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly object _gate = new();

    public int Dim => _extractor.Dim;

    public SpeakerEmbedder(string embeddingModel, int threads = 1)
        => _extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = threads });

    /// <param name="samples16k">Mono 16 kHz float audio, at least about 0.5 s of speech.</param>
    public float[] Embed(float[] samples16k)
    {
        lock (_gate) // one extraction at a time: the native extractor is shared
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(16000, samples16k);
            stream.InputFinished();
            return _extractor.Compute(stream);
        }
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }

    public static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var m = new float[vectors[0].Length];
        foreach (var v in vectors)
            for (int i = 0; i < m.Length; i++) m[i] += v[i] / vectors.Count;
        return m;
    }

    public void Dispose() => _extractor.Dispose();
}
