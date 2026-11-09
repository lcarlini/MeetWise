# Hands-on test pass (2026-11-07)

The real app was driven through UI Automation (`tools/ui-test.ps1`, helpers in `tools/ui-lib.ps1`) against a scratch data folder, with real microphones and speakers (USB Advanced Audio Device, NVIDIA Broadcast).

## Bugs found and fixed

| Bug | How it showed | Fix |
|---|---|---|
| The window froze when recording started and a capture device was slow or stuck | Clicking Record left the app "not responding" for good (a WASAPI start ran on the UI thread, in `RecordingSession.StartAsync`) | Devices open on a worker thread with a 15 s limit; on timeout the recording is abandoned with a clear message and any device the stuck call grabs later is released (`RecordingSessionTests`) |
| Same risk when starting the MeetWise Microphone route | `EnhancementController.StartAsync` opened devices on the UI thread | Opens on a worker thread |
| Cryptic audio errors ("Class not registered (0x80040154)") | Shown verbatim in the status line | `AudioErrors.Describe` explains the cause and what to do (`AudioErrorsTests`) |
| Combo box items read as `Choice { Value = en, Text = English }` to screen readers and automation | Found while driving the combos | The choice records print their text |
| A live subtitle could grow to 20 s of text | Continuous speech made one unreadable paragraph | The streaming recogniser now closes an utterance after 12 s |

## Scenarios covered by `tools/ui-test.ps1` (27 checks, EN/PT/ES)
Startup under 8 s; all five pages open and show their own controls; record page controls; microphone list never empty; readable item names.

## Recording, end to end (real audio)
English, system audio from a playback of known speech, 20 s: live subtitles appeared while recording, stop produced `.txt` and `.srt` with the spoken words.

## Known, not fixed
- Windows audio went down once during testing (Windows Audio service crash while a stuck device call was being killed) and recovered by itself after a minute. The app now survives that case, but the cause is a driver or service issue outside MeetWise.
- "(primary)", "Default Windows microphone" and the Meetings page text stay English in PT/ES.
- Not run here: installing the MeetWise Microphone (needs the user to approve the Windows prompt), video recording, imports and the AI Assistant through the UI, and a live call.

---

# Quality and performance pass (2026-11-09)

Measured with the new `MeetWise.App.exe --benchmark` (the same code as the "Try it yourself" panels) on an RTX 4070 Laptop (8 GB), 16 threads, GPU pack installed. Test clip: three spoken sentences (Windows voice, 16.3 s) with a 50 Hz hum and hiss, so the spoken text is known.

## Bugs and slowdowns found, and what changed

| Finding | Measured | Fix |
|---|---|---|
| The app froze when voice training was closed (the cleanup route restarts) | UI thread blocked for good; thread stacks showed `LoopbackReference.Dispose` joining the capture thread while holding the lock its `RecordingStopped` handler needs | Dispose joins the capture thread outside the lock; stopping the route runs off the UI thread |
| App would not reopen after closing | A hidden copy kept running in the tray; a second launch hit the single-instance lock and quit | A second launch now shows the running window; the tray has Open/Exit; the process exits when the main window closes |
| Idle CPU about 220 % of one core | Voice lock at 120 to 300 % of real time, mic card re-reading device properties every 3 s on the UI thread, load meter stuck at NaN so the automatic downgrade never fired | Below |
| Voice lock too slow for live use | **301 % of real time** with 2 threads and 10 ms blocks. Its model costs a fixed ~30 to 40 ms per call (2 threads: 20 ms blocks 169 %, 40 ms 96 %, 100 ms 52 %; 4 threads: 132 %, 74 %, 38 %) | Input is gathered into 80 ms blocks (`EnhancementOptions.MinBlockMs`) and it uses 2 to 4 threads: **43 %** of real time |
| Load meter showed `NaN` | A zero-length audio block poisoned the moving average for the whole session | Empty blocks are ignored; the load is sampled once per processed block, weighted by the audio it covered |
| The overload fallback could not help voice lock | It lowered the noise mode, which voice lock ignores | It now pauses voice lock for the session (the saved setting is kept) and tells the user |
| Whisper in the test bench ran on the CPU path | 16.7 s for a 16.3 s clip, because the CUDA libraries were not activated | `CudaRuntimePack.Activate()` before loading: **0.6 s** |
| Training sentences invisible in dark mode | The sentence box had a fixed light background under the theme's white text | It uses the theme's card style |
| Quality drop-down looked stuck | It is disabled outside video modes, with no explanation | The label says "Quality (video modes only)"; the saved value is no longer overwritten |

## Benchmark results (after)

| Test | Result |
|---|---|
| Original clip | background -33.4 dB, voice -27.1 dB, clarity 6 dB |
| Noise Light | background -44.0 dB, voice -2.5 dB, clarity 48 dB, 6 % of real time |
| Noise Balanced | background -62.3 dB, voice -1.8 dB, clarity 67 dB, 17 % of real time |
| Noise Max | background -71.6 dB, voice -1.7 dB, clarity 76 dB, 34 % of real time |
| Voice lock (clip is not the enrolled voice) | 43 % of real time (was 301 %) |
| Live subtitle engine (English) | 4.7 s, 24 % word errors |
| Whisper on the GPU | 0.6 s, 3 % word errors |
| Whisper after Balanced clean-up | 0.5 s, 3 % word errors |
| Speaker turns | 2.4 s |
| Video encoders | NVENC (chosen), AMD AMF, x264 |

One synthetic clip on one PC: the point of the Try panels is that anyone can repeat this with their own voice and room. Not measured: real room echo (needs a clip recorded with "also record what the PC plays" on speakers), the AI-model tests in the quick benchmark (`--quick` skips them).

## New tests
`AudioMetricsTests` (noise removed, voice change across sample rates, word error rate, percentiles, ERLE), `Mux_EmbedsSubtitleTrack_WhenSrtGiven` (real FFmpeg, `mov_text` track in the MP4), `Engine_GathersInputIntoLargerBlocks_AndLosesNoAudio` (batching keeps the audio and never reports a NaN load), and `LocalizationTests` now cover about 140 tooltips and the Try panel in English, Portuguese and Spanish.

## Driving the real window
`tools/try-drive.ps1` opens a page, picks a test, runs it on the benchmark clip, waits for the result and saves a screenshot (also dialogs and tooltips); `tools/capture-docs.ps1` runs it for every picture of `docs/index.html` in English and Portuguese.

---

# "No filter" with voice lock on (2026-11-09, afternoon)

Reported: OBS on the MeetWise Microphone and the compare box sounded unfiltered.

Cause: with voice lock on, the chain was voice lock only and the selected noise mode was never applied. Voice lock keeps the enrolled voice but does not remove steady noise. A second, separate cause of the silence in OBS earlier that day: the screenshot scripts closed the running MeetWise (they now refuse to run while it is open).

Measured on the user's own 5.4 s clip ("testing, testing…"; background -52 dB). Background reduction comes from the Try metrics; the text is what Whisper heard in each processed version (`--dump`, then each file through `--benchmark`):

| Chain | Background reduction | Whisper heard | Real time used (16 kHz test) |
|---|---|---|---|
| Original | - | "Testing, testing, testing, testing." | - |
| Noise Max only | 74 dB | "testing testing testing testing" | 30 % |
| Voice lock only (what ran before the fix) | 16 dB | "Dasty, tasty, tasty, tasty." | 33-42 % |
| Voice lock + noise Balanced | 72 dB | "Dasty, nasty, nasty, nasty." | 55-61 % |
| Voice lock + noise Max (now) | 80 dB | "There is thing, there is thing…" | 70-76 % |
| Noise Max, then voice lock | - | "Dusting, dusting, dusting, dusting." | - |

Fix: the selected noise mode now always runs after voice lock (`EnhancementOptions.PostKind`), the mic card and the test box say what is applied, and the overload fallback lowers the noise stage before pausing voice lock. Live with voice lock + Max + echo: 67 % of real time, 0 dropouts, about 4 CPU cores. New test `VoiceLockChain_AlsoRemovesSteadyNoise_AndKeepsMyVoice`.

Not fixed: on this user's voice, voice lock (int8 VoxLock model) makes words unclear in every order tried. The test box now warns when voice lock is on. Recommendation: turn voice lock off, or train the voice again and compare by ear.

