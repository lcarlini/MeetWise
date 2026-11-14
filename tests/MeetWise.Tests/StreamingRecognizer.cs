using SherpaOnnx;

namespace MeetWise.Speech;

public sealed record TranscriptEvent(string Channel, bool IsFinal, string Text, double StartSec, double EndSec);

/// <summary>
/// Streaming ASR for one audio channel (for example Me or Others). Feed raw device buffers via Feed;
/// partial and final results arrive through the Transcript event. Endpointing closes each utterance.
/// </summary>
public sealed class StreamingRecognizer : IDisposable
{
    private readonly OnlineRecognizer _rec;
    private readonly OnlineStream _stream;
    private readonly string _channel;
    private readonly Resampler _resampler;
    private double _samplesFed;
    private double _utteranceStart;
    private string _lastPartial = "";

    public event Action<TranscriptEvent>? Transcript;

    public StreamingRecognizer(string modelDir, string channel, int sourceRate, int sourceChannels,
        int threads = 2, string provider = "cpu")
    {
        _channel = channel;
        _resampler = new Resampler(sourceRate, sourceChannels);
        var cfg = new OnlineRecognizerConfig();
        cfg.FeatConfig.SampleRate = 16000;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Transducer.Encoder = Find(modelDir, "encoder");
        cfg.ModelConfig.Transducer.Decoder = Find(modelDir, "decoder");
        cfg.ModelConfig.Transducer.Joiner = Find(modelDir, "joiner");
        cfg.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        cfg.ModelConfig.NumThreads = threads;
        cfg.ModelConfig.Provider = provider;
        cfg.DecodingMethod = "greedy_search";
        cfg.EnableEndpoint = 1;
        cfg.Rule1MinTrailingSilence = 2.4f;
        cfg.Rule2MinTrailingSilence = 0.8f;
        cfg.Rule3MinUtteranceLength = 12f; // a live subtitle never grows past about 12 s of continuous speech (it was 20 s: one unreadable paragraph)
        _rec = new OnlineRecognizer(cfg);
        _stream = _rec.CreateStream();
    }

    /// <summary>Finds the model part by prefix (encoder, decoder, joiner), preferring full-precision weights.</summary>
    private static string Find(string dir, string prefix)
    {
        var files = Directory.GetFiles(dir, prefix + "*.onnx");
        var pick = files.FirstOrDefault(f => !f.Contains(".int8.")) ?? files.FirstOrDefault();
        return pick ?? throw new FileNotFoundException($"No {prefix}*.onnx in {dir}");
    }

    /// <summary>Feed interleaved float32 PCM at the source rate given to the constructor.</summary>
    public void Feed(ReadOnlySpan<float> interleaved)
    {
        var mono = _resampler.Process(interleaved);
        if (mono.Length == 0) return;
        _stream.AcceptWaveform(Resampler.TargetRate, mono);
        _samplesFed += mono.Length;
        Pump();
    }

    /// <summary>Flush the tail of the last utterance (call when the meeting stops).</summary>
    public void Finish()
    {
        _stream.AcceptWaveform(Resampler.TargetRate, new float[Resampler.TargetRate / 2]);
        _samplesFed += Resampler.TargetRate / 2;
        _stream.InputFinished();
        Pump(forceFinal: true);
    }

    private void Pump(bool forceFinal = false)
    {
        while (_rec.IsReady(_stream)) _rec.Decode(_stream);
        var text = _rec.GetResult(_stream).Text.Trim();
        double now = _samplesFed / Resampler.TargetRate;
        // Only close an utterance that has text: resetting on an empty endpoint (leading silence or the
        // model's start-up blanks) discards audio that is still being decoded and loses the first words.
        if ((_rec.IsEndpoint(_stream) && text.Length > 0) || forceFinal)
        {
            if (text.Length > 0)
                Transcript?.Invoke(new TranscriptEvent(_channel, true, text, _utteranceStart, now));
            _rec.Reset(_stream);
            _utteranceStart = now;
            _lastPartial = "";
        }
        else if (text.Length > 0 && text != _lastPartial)
        {
            _lastPartial = text;
            Transcript?.Invoke(new TranscriptEvent(_channel, false, text, _utteranceStart, now));
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _rec.Dispose();
    }
}
