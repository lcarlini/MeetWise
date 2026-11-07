using MeetWise.Audio;
using MeetWise.Core.Models;
using MeetWise.Intelligence;
using MeetWise.Speech;
using MeetWise.Storage;

namespace MeetWise.Tests;

/// <summary>
/// Builds a demo data folder for documentation screenshots, using the real engines on the sample speech that ships with the speech models:
/// Whisper transcribes it, a local Qwen3 model writes the chat replies. Runs only when MEETWISE_SEED_DEMO names a folder, so a normal test run skips it.
/// </summary>
public class DemoDataTests
{
    private static string Models => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetWise", "models");

    [Fact]
    public async Task DemoData_IsSeeded_FromRealTranscriptionAndARealModel_WhenRequested()
    {
        var target = Environment.GetEnvironmentVariable("MEETWISE_SEED_DEMO");
        if (string.IsNullOrWhiteSpace(target)) return;

        var whisperModel = Path.Combine(Models, "whisper", "ggml-base.bin");
        var wavs = Path.Combine(Models, "asr", "sherpa-onnx-streaming-zipformer-en-2023-06-26", "test_wavs");
        var mandarin = Path.Combine(Models, "speaker", "four-speakers.wav");
        Assert.True(File.Exists(whisperModel) && Directory.Exists(wavs) && File.Exists(mandarin), "the sample speech and the Whisper base model are needed");

        Directory.CreateDirectory(target);
        var dbPath = Path.Combine(target, "meetwise.db");
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
        var db = new MeetingDatabase(dbPath);
        var recordings = Path.Combine(target, "recordings");
        Directory.CreateDirectory(recordings);

        MeetWise.Core.Models.CudaRuntimePack.Activate();
        var ffmpeg = await FFmpegPack.EnsureAsync(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

        // three English sample clips joined into one 40-second "recording"
        var demoWav = Path.Combine(recordings, "product-walkthrough.wav");
        var join = $"-hide_banner -loglevel error -y -i \"{Path.Combine(wavs, "0.wav")}\" -i \"{Path.Combine(wavs, "1.wav")}\" -i \"{Path.Combine(wavs, "8k.wav")}\" " +
                   "-filter_complex \"[0:a]aresample=16000[a0];[1:a]aresample=16000[a1];[2:a]aresample=16000[a2];[a0][a1][a2]concat=n=3:v=0:a=1[o]\" " +
                   $"-map \"[o]\" -c:a pcm_s16le \"{demoWav}\"";
        Assert.Equal(0, (await FFmpegPack.RunAsync(ffmpeg, join)).ExitCode);

        using var whisper = new WhisperTranscriber(whisperModel);
        var importer = MediaImporter.WithWhisper(ffmpeg, whisper);
        var outDir = Path.Combine(recordings, "Subtitles");

        var english = await importer.ImportAsync(demoWav, outDir, new ImportOptions(Language: "en"));
        var rec = db.AddMeeting("Product walkthrough", DateTime.UtcNow.AddHours(-2), audioPath: demoWav, kind: "recording", source: "Display 1 (2752×1152)");
        db.ReplaceSegments(rec, english.Cues.Select(c => ("Me", c.Start, c.End, c.Text, c.Language)));

        var zh = await importer.ImportAsync(mandarin, outDir, new ImportOptions(Language: "auto"));
        var imp = db.AddMeeting("Interview (Mandarin)", DateTime.UtcNow.AddDays(-1), audioPath: mandarin, kind: "import", source: mandarin);
        db.ReplaceSegments(imp, zh.Cues.Select(c => ("Speaker", c.Start, c.End, c.Text, c.Language)));
        Assert.NotEmpty(english.Cues);
        Assert.NotEmpty(zh.Cues);

        // a short real conversation with the local model about the English recording
        var gguf = Path.Combine(Models, "llm", "Qwen3-8B-Q4_K_M.gguf");
        if (File.Exists(gguf))
        {
            using var llm = new LocalLlamaProvider(gguf, contextTokens: 4096, gpuLayers: 99);
            var assistant = new TranscriptAssistant(llm);
            var lines = db.GetTranscript(rec).Select(s => new AssistantLine(s.StartSec, s.Speaker, s.Text)).ToList();
            db.AddAiMessage(rec, "user", "Summarize");
            db.AddAiMessage(rec, "assistant", await assistant.RunAsync(lines, new AssistantRequest(AssistantCommand.Summarize)));
            db.AddAiMessage(rec, "user", "Chapters");
            db.AddAiMessage(rec, "assistant", await assistant.RunAsync(lines, new AssistantRequest(AssistantCommand.Chapters)));
        }
    }
}
