using SherpaOnnx;

namespace MeetWise.Speech;

/// <summary>Restores capitalisation and punctuation in recognizer output ("how are you i am fine" becomes "How are you? I am fine.").</summary>
public sealed class EnglishPunctuator : IDisposable
{
    private readonly OnlinePunctuation _punct;
    private readonly object _gate = new();

    public EnglishPunctuator(string modelDir, int threads = 1)
    {
        var cfg = new OnlinePunctuationConfig();
        cfg.Model.CnnBiLstm = Path.Combine(modelDir, "model.int8.onnx") is var q && File.Exists(q) ? q : Path.Combine(modelDir, "model.onnx");
        cfg.Model.BpeVocab = Path.Combine(modelDir, "bpe.vocab");
        cfg.Model.NumThreads = threads;
        _punct = new OnlinePunctuation(cfg);
    }

    public string Restore(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        lock (_gate) return _punct.AddPunct(text.ToLowerInvariant()).Trim();
    }

    public void Dispose() => _punct.Dispose();
}
