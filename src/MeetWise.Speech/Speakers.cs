using System.Text.Json;
using SherpaOnnx;

namespace MeetWise.Speech;

public sealed record DiarSegment(double Start, double End, int Speaker);

/// <summary>Offline "who spoke when": pyannote segmentation plus speaker embeddings plus clustering. Run after the meeting on the remote-audio channel.</summary>
public sealed class Diarizer : IDisposable
{
    private readonly OfflineSpeakerDiarization _sd;

    public Diarizer(string segmentationModel, string embeddingModel, int numSpeakers = -1, float threshold = 0.5f, int threads = 2)
    {
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = segmentationModel;
        cfg.Segmentation.NumThreads = threads;
        cfg.Embedding.Model = embeddingModel;
        cfg.Embedding.NumThreads = threads;
        cfg.Clustering.NumClusters = numSpeakers;
        cfg.Clustering.Threshold = threshold;
        cfg.MinDurationOn = 0.3f;
        cfg.MinDurationOff = 0.5f;
        _sd = new OfflineSpeakerDiarization(cfg);
        SampleRate = _sd.SampleRate;
    }

    public int SampleRate { get; }

    public IReadOnlyList<DiarSegment> Diarize(float[] samples16k)
        => _sd.Process(samples16k).Select(s => new DiarSegment(s.Start, s.End, s.Speaker)).OrderBy(s => s.Start).ToList();

    public void Dispose() => _sd.Dispose();
}

public static class SpeakerAssigner
{
    /// <summary>For each utterance, the diarized speaker with the largest time overlap, or -1 when nobody overlaps it.</summary>
    public static int[] Assign(IReadOnlyList<(double Start, double End)> utterances, IReadOnlyList<DiarSegment> diar)
    {
        var result = new int[utterances.Count];
        for (int i = 0; i < utterances.Count; i++)
        {
            var (us, ue) = utterances[i];
            var overlap = new Dictionary<int, double>();
            foreach (var d in diar)
            {
                if (d.End <= us) continue;
                if (d.Start >= ue) break; // diar is sorted by start
                double o = Math.Min(ue, d.End) - Math.Max(us, d.Start);
                if (o > 0) overlap[d.Speaker] = overlap.GetValueOrDefault(d.Speaker) + o;
            }
            result[i] = overlap.Count == 0 ? -1 : overlap.MaxBy(kv => kv.Value).Key;
        }
        return result;
    }
}

/// <summary>
/// Remembers voices (opt-in, stored only on this PC) so speakers can be suggested by name across meetings.
/// Embeddings are stored, never recordings.
/// </summary>
public sealed class SpeakerProfiles : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly SpeakerEmbeddingManager _manager;
    private readonly string _file;
    private readonly Dictionary<string, List<float[]>> _voices = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _voices.Keys;

    public SpeakerProfiles(string embeddingModel, string storeFile, int threads = 1)
    {
        var cfg = new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = threads };
        _extractor = new SpeakerEmbeddingExtractor(cfg);
        _manager = new SpeakerEmbeddingManager(_extractor.Dim);
        _file = storeFile;
        if (File.Exists(storeFile))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(storeFile));
            if (saved is not null)
                foreach (var (name, list) in saved)
                    foreach (var emb in list) AddEmbedding(name, emb);
        }
    }

    public float[] Embed(float[] samples16k)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(16000, samples16k);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    private void AddEmbedding(string name, float[] emb)
    {
        if (!_voices.TryGetValue(name, out var list)) _voices[name] = list = new();
        list.Add(emb);
        _manager.Remove(name);
        _manager.Add(name, list); // the manager averages all samples of one speaker
    }

    public void Enroll(string name, float[] samples16k)
    {
        AddEmbedding(name, Embed(samples16k));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_voices));
    }

    /// <summary>Best matching known name above the threshold, or null.</summary>
    /// <remarks>Measured cosine scores with the CAM++ model: same voice 0.96, different voices 0.54-0.75, so 0.8 separates them with margin.</remarks>
    public string? Identify(float[] samples16k, float threshold = 0.8f)
    {
        if (_voices.Count == 0) return null;
        var name = _manager.Search(Embed(samples16k), threshold);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public void Forget(string name)
    {
        _voices.Remove(name);
        _manager.Remove(name);
        File.WriteAllText(_file, JsonSerializer.Serialize(_voices));
    }

    public void Dispose()
    {
        _extractor.Dispose();
        _manager.Dispose();
    }
}
