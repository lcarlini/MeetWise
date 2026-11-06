using SherpaOnnx;

namespace MeetWise.Speech;

public enum EnhancerKind
{
    /// <summary>GTCRN: tiny (0.5 MB), 16 kHz, runs on any CPU.</summary>
    Light,
    /// <summary>DPDFNet 48 kHz full-band (DeepFilterNet family): best quality for calls.</summary>
    FullBand,
    /// <summary>VoxLock target-speaker extraction: keeps only the enrolled voice (voice isolation).</summary>
    VoiceIsolation
}

/// <summary>
/// Streaming noise suppression / voice isolation. Input and output are mono float PCM; the model
/// resamples internally, so any capture rate can be passed to <see cref="Process"/>.
/// </summary>
public sealed class SpeechEnhancer : IDisposable
{
    private readonly OnlineSpeechDenoiser _denoiser;

    public int OutputSampleRate => _denoiser.SampleRate;
    public int FrameShift => _denoiser.FrameShiftInSamples;

    /// <param name="modelPath">Light/FullBand: the .onnx file. VoiceIsolation: the extracted voxlock folder.</param>
    /// <param name="voiceSampleWav">VoiceIsolation only: a wav of the target speaker (enrollment).</param>
    public SpeechEnhancer(EnhancerKind kind, string modelPath, int threads = 1, string? voiceSampleWav = null, string provider = "cpu")
    {
        var cfg = new OnlineSpeechDenoiserConfig();
        cfg.Model.NumThreads = threads;
        cfg.Model.Provider = provider;
        switch (kind)
        {
            case EnhancerKind.Light:
                cfg.Model.Gtcrn.Model = modelPath;
                break;
            case EnhancerKind.FullBand:
                cfg.Model.Dpdfnet.Model = modelPath;
                break;
            case EnhancerKind.VoiceIsolation:
                if (voiceSampleWav is null) throw new ArgumentException("Voice isolation needs an enrollment sample.", nameof(voiceSampleWav));
                cfg.Model.Voxlock.EnrollModel = FindFile(modelPath, "enroll");
                cfg.Model.Voxlock.ChunkModel = FindFile(modelPath, "chunk");
                cfg.Model.Voxlock.VoiceSample = voiceSampleWav;
                break;
        }
        _denoiser = new OnlineSpeechDenoiser(cfg);
    }

    private static string FindFile(string dir, string key)
        => Directory.GetFiles(dir, "*.onnx").FirstOrDefault(f => Path.GetFileName(f).Contains(key, StringComparison.OrdinalIgnoreCase))
           ?? throw new FileNotFoundException($"No *{key}*.onnx in {dir}");

    /// <summary>Denoises a block. Output lags input by a model-dependent look-ahead and may be shorter than the input.</summary>
    public float[] Process(float[] mono, int sampleRate) => _denoiser.Run(mono, sampleRate).Samples;

    /// <summary>Returns the tail still buffered inside the model (call at end of stream).</summary>
    public float[] Flush() => _denoiser.Flush().Samples;

    public void Reset() => _denoiser.Reset();

    public void Dispose() => _denoiser.Dispose();
}
