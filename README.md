<p align="center"><img src="docs/img/icon.png" width="96" alt="MeetWise icon"></p>

# MeetWise

**Product page: [lcarlini.github.io/MeetWise](https://lcarlini.github.io/MeetWise/)**, with screenshots and a full walkthrough of the app.

A local-first AI meeting assistant for Windows 11. It captures conversations without a bot, transcribes them live, cleans up your microphone, and turns meetings into notes, action items and answers. Everything runs on your PC; an API key is an optional upgrade, never a requirement.

Built for any machine, and fast on NVIDIA GPUs (developed on an RTX 4070 Laptop, 8 GB).

## MeetWise Studio: record, subtitle, ask the AI

One window, five pages. It brings in what used to be two separate tools: **RecBox** (screen recording) and **LanguageAwareSubtitleExtractor** (language-aware subtitles).

| Page | What you do there |
|---|---|
| **Record** | Pick a monitor (native resolution), microphone, system audio, quality (Low file size / Balanced / Maximum) and mode (video + audio, audio only, transcript only). Start and stop with the big button or **Ctrl+Shift+R**. Subtitles appear **while you record**, in any language (English streams word by word; other languages are cut at pauses and transcribed with Whisper on the GPU; "Subtitles in English" translates). An optional floating caption overlay is excluded from the recording. MeetWise's own windows are kept out of the video. |
| **Subtitles** | After you stop: tick the lines you want (all / none / invert / filter), edit them, then **Copy text**, **Copy as SRT**, or save **SRT / VTT / TXT** (selected or all). "Refine with Whisper" re-runs the whole recording for maximum accuracy. "Ask AI about selection" sends the selected lines to the assistant. |
| **AI Assistant** | Chat about a recording (or only the lines you selected): Summarize, Key points, Action items, Chapters, Title and description, Follow-up email, Clean up text, Translate to a language, or any free-text command. Replies can be copied or saved. Local Qwen3 by default, API key optional. |
| **Library** | Every recording, import and meeting, with full-text search across transcripts. Import audio/video files (language, audio-track choice by language, noise filter, `.txt` always plus optional `.srt`), import a **YouTube** link (captions first, local transcription as fallback), or **watch a folder** (`To Process` to `Output`, originals moved to `Processed`, failures to `Errors`). |
| **Meetings** | The existing meeting tools: MeetWise Microphone, voice lock, English coach, notes, Ask, action items, calendar. |

The interface speaks English, Portuguese and Spanish (switch at the bottom of the navigation rail). Screenshots are in `docs/img` and are produced by `tools/capture-screens.ps1` against demo data, so your own recordings never appear in them.

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
| | **MeetWise Microphone**: a real microphone in Windows Sound settings that every app can use, fed with your cleaned voice | One click installs VB-Audio's signed VB-CABLE (one Windows prompt) and names it "MeetWise Microphone"; the route heals itself on device changes and never records its own output |
| | **Voice lock**: guided training on six sentences, then only your voice passes | Quality checks per sentence, VoxLock target-speaker extraction; measured 19-47 dB removal of a second speaker, own voice within 1 dB |
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
| English coach | Private on-screen corrections of your spoken English, floating over any meeting app, plus a speaking report (repeated mistakes, pace) | Punctuation restoration + local Qwen3-8B; on 15 sentences with real mistakes it caught 15 and raised 0 false alarms on 15 correct ones |
| AI engine | Local by default. Optional Anthropic API key, or any OpenAI-compatible server (OpenAI, Ollama, LM Studio) | Keys are encrypted with Windows DPAPI, never in settings.json |

## What is not done (and why)

- **MeetWise's own virtual-audio driver.** The MeetWise Microphone is created with VB-Audio's signed VB-CABLE, because a virtual device is a kernel driver and Windows with Secure Boot only loads signed drivers. A driver of our own would need the Windows Driver Kit, an EV code-signing certificate and Microsoft attestation signing (see `driver/README.md`). VB-CABLE is donationware and its licence does not allow bundling it in another installer, so MeetWise downloads the unmodified pack from vb-audio.com when you click the button, and credits VB-Audio in the app.
- **Cleaning the other participants' audio** before it reaches your speakers (it needs the virtual speaker side routed through MeetWise; only your microphone is cleaned today).
- **True accent conversion** (changing how a voice sounds while preserving the voice itself in real time). There is no usable streaming model for this. The nearest thing available is the "re-voice" mode (transcribe, then speak with a Windows voice), which has about a second of delay and does not keep your own voice.
- **Speech translation quality equal to NLLB/Seamless.** Translation uses the configured LLM; with the 8B local model it is good for common languages and weaker for rare ones.
- **Windows voices** for spoken translation only exist for the languages installed in Windows (Settings, Time and language, Speech).
- Outbound phone-call recording, the mobile app, a meeting-bot option, NVIDIA Maxine, trimming/clipping UI, comments, and UI for tags/folders/renaming speakers. The storage layer for tags, folders and series exists; the screens do not.
- A setup.exe: the script and Inno Setup file are in place (`build-installer.ps1`, `installer\MeetWise.iss`) but Inno Setup 6 is not installed here, so only the portable folder/zip was built and verified.

## How much of this is verified

Tested automatically (286 tests, all passing on the development machine) against real models and real devices on Windows 11 with an RTX 4070 Laptop: streaming and Whisper transcription (CPU and GPU), noise suppression and voice isolation (noise reduction in dB, speech still recognised), echo cancellation (ERLE), diarization and speaker identification, local LLM on the GPU, notes/Ask/briefs/translation prompts (with a scripted LLM), calendar parsing, FFmpeg recording of the real screen with NVENC and MP4 muxing, crash recovery, download resume, device recording, and a self-test of the published build. Studio-specific checks: subtitle formatting (SRT/VTT round trips, YouTube rolling-caption clean-up), live subtitles on real speech in English and Mandarin (auto-detect and translate to English), audio-track selection and chunk offsets on generated multi-track files, the watch-folder flow, a real 5 s screen recording that yields an MP4 with matching `.srt`/`.txt`, the AI assistant against a scripted model plus a real Qwen3-8B smoke test, the schema upgrade, and that every UI string exists in English, Portuguese and Spanish. The UI was launched and every page was captured and inspected.

Also verified on the real machine: the voice-lock measurements, the coach evaluation (15/15 and 0/15 above), the self-healing route (restart, backoff, device-change debouncing), the Windows default-microphone switch (set to itself and restored), the VB-CABLE pack download with its pinned SHA-256, and the database upgrade of an existing version-1 file.

Implemented but **not** exercised end to end, because the YouTube download needs the network and a JavaScript runtime (Deno or Node), so only parsing, argument building and hash checking are tested offline; the Studio window was not driven through a full record-and-stop by UI automation (the same engine is tested directly); and they need hardware or accounts this environment lacks: running the VB-CABLE installer itself (it needs a Windows administrator prompt, so it was not run unattended), renaming the new endpoints to "MeetWise Microphone" (the same property-store call was only read-tested; it is never written by tests), hearing the cleaned microphone in a real call, real room echo, the automatic downgrade under real CPU overload, spoken translation into a virtual microphone, the Anthropic/OpenAI network calls (tested against a stub server only), and live calendar feeds.

## Design notes

- **Why voice lock is VoxLock and not a voiceprint gate.** A voiceprint (CAM++) needs about 3 s of speech: on 1 to 1.5 s windows, same-speaker scores (0.29-0.60) overlap different-speaker scores (0.15-0.59), so a real-time identity gate would cut off the first words of the user. VoxLock extracts the enrolled voice directly and was measured instead. A loudness gate on top of it removed nothing (leaked sound and quiet word endings sit within about 17 dB of each other), so it was dropped.
- **Why the coach ignores capitalisation and recognition errors.** Its input is speech-recognition text, so the prompt tells the model to report only real grammar and word-choice mistakes, and an "unchanged" correction is discarded.
- **Safety nets for a system-wide microphone.** The route never uses the virtual mic as its own input; the app warns before closing while the MeetWise Microphone is the Windows default (apps would hear silence) and offers to restore your previous microphone.

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

Models download on first use (resumable, SHA-256 verified) into `%LocalAppData%\MeetWise`. Settings and voices live in `%AppData%\MeetWise`. Set `MEETWISE_DATA` to use a different data folder (used for the demo screenshots).

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
src/MeetWise.App           WPF Studio shell and pages (Record, Subtitles, AI Assistant, Library, Meetings)
tests/MeetWise.Tests       about 290 tests, most run against real models and the real audio/video devices
```

## Privacy

Audio is processed on this PC. Network use: model and runtime downloads (GitHub, Hugging Face, NVIDIA, Gyan.dev), the calendar feed you configure, and the LLM endpoint only if you choose a cloud provider or server. Voice profiles are stored as numeric embeddings, never as recordings, and can be removed with SpeakerProfiles.Forget (no screen for it yet).

Recording other people may require their consent where you live. MeetWise does not announce itself in the meeting, so you are responsible for telling participants.
