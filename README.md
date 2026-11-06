# MeetWise

A local-first AI meeting assistant for Windows 11. It captures conversations without a bot, transcribes them live, cleans up your microphone, and turns meetings into notes, action items and answers. Everything runs on your PC; an API key is an optional upgrade, never a requirement.

Built for any machine, and fast on NVIDIA GPUs (developed on an RTX 4070 Laptop, 8 GB).

## What works today

| Area | Feature | How |
|---|---|---|
| Capture | Bot-free recording of your microphone and the system audio as two separate channels ("Me" and "Others") | WASAPI + loopback, WAV flushed every second (a crash loses about 1 s) |
| | Audio + video recording, crash recovery | FFmpeg screen capture (NVENC, Quick Sync, AMF, x264 are probed), MKV then MP4 mux, orphaned captures are finished on the next start |
| | Import any audio or video file | Windows Media Foundation decoding, same transcription pipeline |
| Live transcript | Streaming speech to text with partial results, timestamps and speaker channel | sherpa-onnx streaming Zipformer, one recognizer per channel, off the audio thread |
| | High-accuracy "Refine" pass, 99 languages, custom vocabulary as a prompt | Whisper large-v3-turbo on the GPU (smaller models on weaker PCs) |
| | Speaker separation inside "Others" and names for enrolled voices | pyannote segmentation + CAM++ embeddings, voices stored as embeddings only |
| | Custom vocabulary with starter kits (software, sales, medical, legal) | Whisper prompt + near-miss correction of live text |
| Audio enhancement | Noise suppression: Light, Balanced, Max | GTCRN (0.5 MB), DPDFNet 16 kHz, DPDFNet 48 kHz full-band |
| | Echo cancellation | NLMS adaptive filter with double-talk protection |
| | Voice isolation (keeps only you) | VoxLock target-speaker extraction with a 10 s enrollment |
| | Automatic downgrade if the CPU cannot keep up | `AudioRoute.Overloaded` switches Max to Balanced to Light |
| Intelligence | Notes, key points, decisions, risks, action items with owners and dates | Local Qwen3 (8B on 8 GB GPUs, 4B otherwise), long meetings are chunked and merged |
| | Templates: general, standup, client call, interview, sales, incident, 1:1 | Prompt templates, personalised with your role |
| | Ask MeetWise: questions across all meetings with timestamp citations | Full-text retrieval then LLM answer grounded in the excerpts |
| | Central action list, tags, folders, recurring series | SQLite + FTS5 |
| | Webhook export of action items | Plain JSON POST |
| Pre-meeting | Calendar from any iCal feed (Google, Outlook, Apple), recurring events | ICS parser with RRULE and time zones |
| | Reminders, "record this call?" prompt when Zoom/Teams/Meet use the mic, agenda and brief from earlier meetings of the same series | Windows microphone consent store, LLM |
| Translation | Live captions of other people in your language | LLM translation |
| | Speak your sentences translated, or re-voiced in a neutral accent, into a chosen output device | Windows offline voices |
| AI engine | Local by default. Optional Anthropic API key, or any OpenAI-compatible server (OpenAI, Ollama, LM Studio) | Keys are encrypted with Windows DPAPI, never in settings.json |

## What is not done (and why)

- **Virtual microphone/speaker driver ("MeetWise Microphone")**. This is a kernel driver that needs the Windows Driver Kit, an EV code-signing certificate and Microsoft attestation signing; none are available in this environment. Until then, cleaned audio is sent to a **virtual cable you install** (the free VB-CABLE works) and you select the cable's other end as the microphone in your meeting app. The app detects whether one is present and tells you what to do. See `driver/README.md`.
- **Cleaning the other participants' audio.** The same virtual-device requirement applies (their audio has to be routed through MeetWise before it reaches your speakers).
- **True accent conversion** (changing how a voice sounds while preserving the voice itself in real time). There is no usable streaming model for this. The nearest thing available is the "re-voice" mode (transcribe, then speak with a Windows voice), which has about a second of delay and does not keep your own voice.
- **Speech translation quality equal to NLLB/Seamless.** Translation uses the configured LLM; with the 8B local model it is good for common languages and weaker for rare ones.
- **Windows voices** for spoken translation only exist for the languages installed in Windows (Settings, Time and language, Speech).
- Outbound phone-call recording, the mobile app, a meeting-bot option, NVIDIA Maxine, trimming/clipping UI, comments, and UI for tags/folders/renaming speakers. The storage layer for tags, folders and series exists; the screens do not.
- A setup.exe: the script and Inno Setup file are in place (`build-installer.ps1`, `installer\MeetWise.iss`) but Inno Setup 6 is not installed here, so only the portable folder/zip was built and verified.

## How much of this is verified

Tested automatically against real models and real devices on Windows 11 with an RTX 4070 Laptop: streaming and Whisper transcription (CPU and GPU), noise suppression and voice isolation (noise reduction in dB, speech still recognised), echo cancellation (ERLE), diarization and speaker identification, local LLM on the GPU, notes/Ask/briefs/translation prompts (with a scripted LLM), calendar parsing, FFmpeg recording of the real screen with NVENC and MP4 muxing, crash recovery, download resume, device recording, and a self-test of the published build.

Implemented but **not** exercised end to end, because they need hardware or accounts this environment lacks: playing the cleaned microphone into a virtual cable and hearing it in a call, real room echo, the automatic downgrade under real CPU overload, spoken translation into a virtual microphone, the Anthropic/OpenAI network calls (tested against a stub server only), and live calendar feeds.

## Hardware tiers

| Tier | Detected as | Defaults |
|---|---|---|
| A | NVIDIA with 6 GB or more VRAM | Whisper turbo and Qwen3-8B on the GPU, Max noise mode available |
| B | Any other GPU | Whisper small, Qwen3-4B (CPU inference unless a CUDA GPU is present) |
| C | CPU only | Whisper base/small, Qwen3-4B on the CPU or an API key |

Live ASR always runs on the CPU, which leaves the GPU free. On 8 GB cards the heavy steps run one after another (the app frees the LLM before Whisper loads).

Measured on the development machine (release build, single thread, 10 ms blocks): Light 0.06, Balanced 0.17, Max 0.29 real-time factor, plus 0.09 for echo cancellation.

## GPU acceleration pack

NVIDIA CUDA libraries (about 400 MB) are downloaded from NVIDIA on request, so no CUDA Toolkit is needed. Without it everything still runs on the CPU. Both Whisper and the local LLM then use CUDA 13 (verified: weights land in VRAM).

## Build and run

Requirements: Windows 11 x64, .NET 10 SDK.

```
dotnet build MeetWise.slnx
dotnet test tests/MeetWise.Tests -c Release
dotnet run --project src/MeetWise.App -c Release
```

Models download on first use (resumable, SHA-256 verified) into `%LocalAppData%\MeetWise`. Settings and voices live in `%AppData%\MeetWise`.

Self-test (also works on a published build, writes a report and returns 0 when all required parts load):

```
MeetWise.App.exe --selftest report.txt
```

Package:

```
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

## Layout

```
src/MeetWise.Core          settings, logging, hardware tiers, model/FFmpeg/CUDA downloads
src/MeetWise.Audio         devices, recorders, enhancement route, video, Windows voices, post-processing
src/MeetWise.Speech        streaming ASR, Whisper, enhancement, echo canceller, diarization, speaker profiles
src/MeetWise.Intelligence  LLM providers, notes, Ask, calendar, briefs, translation, secrets
src/MeetWise.Storage       SQLite + full-text search
src/MeetWise.App           WPF shell
tests/MeetWise.Tests       about 80 tests, most run against real models and the real audio/video devices
```

## Privacy

Audio is processed on this PC. Network use: model and runtime downloads (GitHub, Hugging Face, NVIDIA, Gyan.dev), the calendar feed you configure, and the LLM endpoint only if you choose a cloud provider or server. Voice profiles are stored as numeric embeddings, never as recordings, and can be removed with SpeakerProfiles.Forget (no screen for it yet).

Recording other people may require their consent where you live. MeetWise does not announce itself in the meeting, so you are responsible for telling participants.
