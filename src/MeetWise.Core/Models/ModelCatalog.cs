namespace MeetWise.Core.Models;

/// <summary>Known downloadable models. Archives are extracted into models/&lt;kind&gt;/&lt;folder&gt;.</summary>
public static class ModelCatalog
{
    private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";

    private const string Enh = Release + "speech-enhancement-models/";

    /// <summary>Tiny (0.5 MB) 16 kHz denoiser that runs on any CPU. Light performance mode.</summary>
    public static readonly ModelEntry DenoiseLight = new(
        "gtcrn_simple.onnx", "enhance", Enh + "gtcrn_simple.onnx",
        "e77603ac0c23dac3227dd2d7135b3a585cbee2679048aecfa886657d3ae1b534", 535_638);

    /// <summary>DPDFNet 16 kHz (DeepFilterNet family). Balanced mode: about 3x cheaper than the 48 kHz model.</summary>
    public static readonly ModelEntry DenoiseBalanced = new(
        "dpdfnet2.onnx", "enhance", Enh + "dpdfnet2.onnx",
        "ce35d6025fc71df0ef10d1540e1b7916837bbfe5f6896deb744508d2cad487a9", 10_249_356);

    /// <summary>DPDFNet 48 kHz full-band denoiser (DeepFilterNet family). Max mode.</summary>
    public static readonly ModelEntry DenoiseFullBand = new(
        "dpdfnet2_48khz_hr.onnx", "enhance", Enh + "dpdfnet2_48khz_hr.onnx",
        "0b399f8a58dc4d70d8cd97541f5c39869406145193b957d00a03b66070944928", 10_596_848);

    /// <summary>VoxLock target-speaker extraction for voice isolation (needs a voice enrollment sample).</summary>
    public static readonly ModelEntry VoiceIsolation = new(
        "sherpa-onnx-voxlock-int8", "enhance", Enh + "sherpa-onnx-voxlock-int8.tar.bz2",
        "bcb947f9e8676d3e963f6048b0088505c84f0843bfdc88cb26dde3264d81570b", 69_000_000);

    /// <summary>VoxLock in full precision (fp32, 94 MB). The int8 copy made words unclear on the user's own voice (2026-11-09 notes).</summary>
    public static readonly ModelEntry VoiceIsolationFull = new(
        "sherpa-onnx-voxlock", "enhance", Enh + "sherpa-onnx-voxlock.tar.bz2",
        "1facda8a76bc73ad3f133d560291bc8bc37ca6d9d440ae5f4017d142673f1c33", 94_467_818);

    /// <summary>The voice-lock model the microphone uses. Set MEETWISE_VOXLOCK=int8 to compare against the quantised copy.</summary>
    public static ModelEntry VoiceIsolationFor()
        => Environment.GetEnvironmentVariable("MEETWISE_VOXLOCK") == "int8" ? VoiceIsolation : VoiceIsolationFull;

    public static readonly ModelEntry SpeakerEmbedding = new(
        "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx", "speaker", Release + "speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
        "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b", 29_596_978);

    private const string Ggml = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly ModelEntry WhisperBase = new(
        "ggml-base.bin", "whisper", Ggml + "ggml-base.bin",
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147_951_465);

    public static readonly ModelEntry WhisperSmall = new(
        "ggml-small-q5_1.bin", "whisper", Ggml + "ggml-small-q5_1.bin",
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487);

    public static readonly ModelEntry WhisperTurbo = new(
        "ggml-large-v3-turbo-q5_0.bin", "whisper", Ggml + "ggml-large-v3-turbo-q5_0.bin",
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195);

    /// <summary>High-accuracy multilingual model for the after-meeting pass: large-v3-turbo on 6+ GB NVIDIA cards, smaller elsewhere.</summary>
    public static ModelEntry WhisperFor(MeetWise.Core.Hardware.HardwareProfile hw)
        => hw.Tier switch
        {
            MeetWise.Core.Settings.HardwareTier.A_NvidiaRtx => WhisperTurbo,
            MeetWise.Core.Settings.HardwareTier.B_AnyGpu => WhisperSmall,
            _ => hw.CpuThreads >= 8 ? WhisperSmall : WhisperBase
        };

    /// <summary>Silero voice-activity detector (0.6 MB): checks that voice-training recordings contain enough speech.</summary>
    public static readonly ModelEntry SileroVad = new(
        "silero_vad.onnx", "vad", Release + "asr-models/silero_vad.onnx",
        "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6", 643_854);
}
