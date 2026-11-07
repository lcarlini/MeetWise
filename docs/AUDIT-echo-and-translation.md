# Audit: echo cancellation and live translation

Scope: the echo canceller and its audio plumbing, and the live translation pipeline (captions of other people, spoken translation of the user).
Every issue below was reproduced with a test or measured before it was fixed; the "evidence" column says how. Numbers come from the synthetic signals in
`tests/MeetWise.Tests/EchoSim.cs` (speech-like talkers, room-like paths, noise types) and from the real local model (Qwen3-8B, RTX 4070 Laptop) and the
real Windows voice, so they describe this codebase on this machine, not a lab benchmark. Real room acoustics, Bluetooth hardware and live meeting apps
were **not** available and were **not** tested (see the end).

## 1. Echo cancellation

### Issues found

| # | Severity | Issue | Root cause | Files | User-visible impact | Evidence | Fix |
|---|---|---|---|---|---|---|---|
| E1 | High | The reference is never aligned with the microphone | The loopback samples were queued and read in arrival order; the NLMS filter (100 ms) was the only thing absorbing the delay between the speaker feed and the microphone. Output device buffers, drivers and Bluetooth add 20-300 ms. | `AudioRoute.cs` (`LoopbackReference`), `EchoCanceller.cs` | Echo not cancelled at all on many PCs | ERLE 8.0 dB at 100 ms delay, 5.3 dB at 160 ms, 1.9 dB at 250 ms, 1.3 dB at 400 ms | GCC-PHAT delay estimator (`DelayEstimator.cs`) lines the reference up; ERLE 36-38 dB at every delay from 0 to 400 ms |
| E2 | High | Reference not resampled to the microphone's rate | `LoopbackReference.Read(count)` assumed both streams have the same rate; speakers at 44.1 kHz with a 48 kHz microphone are common | `AudioRoute.cs` | Reference plays at the wrong speed: no cancellation, possible garbage | Code audit; resampler tests (1 kHz tone keeps frequency, duration and level across 44.1/48/16 kHz) | `ReferenceResampler` (WDL), `ReferenceTimeline` |
| E3 | High | The reference slides against the microphone over time | Unbounded `Queue<float>` capped at 1 s, read oldest-first; WASAPI loopback sends **nothing** while the speakers are silent, so when playback resumed its audio was placed straight after the old data | `AudioRoute.cs` | Delay changes by up to a second between sentences; filter keeps relearning | Timeline test: audio after 1 s of silence lands 1 s later | `ReferenceTimeline`: sample-count timeline on wall-clock time, gap filled with zeros, lock-free single-producer/single-consumer ring |
| E4 | Medium | A muted microphone received a ghost | The filter output was `mic - echoEstimate` even for digital silence | `EchoCanceller.cs` | Meeting hears faint far-end speech from a muted microphone | `AMutedMicrophone_StaysSilent...` failed | Mute detection; output stays silent |
| E5 | Medium | A quiet near-end talker corrupted the filter | Per-sample Geigel test only (no hangover, compared with the raw reference peak, assumed echo gain < 1) | `EchoCanceller.cs` | After double talk the echo came back; the user's voice was distorted | Error equal to the talker's level; ERLE 13.5 dB after double talk | Echo gain calibrated in the first second; error-to-microphone energy ratio per 10 ms frame; 150 ms hangover; step size reduced once converged; slowdown relaxes if the room really changed |
| E6 | Medium | The filter adapted without any evidence of an echo | No gate: with headphones, or a reference the microphone cannot hear, NLMS fitted unrelated speech | `EchoCanceller.cs` | The user's own voice made quieter when the reference is unrelated speech (headphones) | Measured at -2.2 dB on an intermediate version without the gate; the original adapted without any gate either | Nothing adapts until the delay estimator has measured the same delay repeatedly; a lock that cancels nothing for 6 s is withdrawn |
| E7 | Medium | The filter did not relearn after a path change | Same alignment problem as E1 plus no recovery logic | `EchoCanceller.cs` | Moving the speaker or changing its volume left the echo uncancelled | ERLE 4.0 dB ten seconds after a path change | ERLE 43.7 dB after the same change |
| E8 | Medium | A loopback failure silently killed echo cancellation | No `RecordingStopped` handling on the loopback capture | `AudioRoute.cs` | After a driver reset or a format change the reference stayed silent for the rest of the call | Code audit | Loopback restarts itself with 1-15 s backoff; the microphone path never depends on it; a loopback that cannot start does not stop the route |
| E9 | Low | No residual echo suppression | Linear filter only | `EchoCanceller.cs` | Reverberant tails audible | Far-end-only ERLE 30.0 dB | Conservative suppressor: at most 15 dB, 20 ms attack, 5 ms release, only when what is left is within 8 dB of what the filter usually leaves; far-end-only ERLE 36.2 dB, near-end level unchanged (-0.1 dB) |
| E10 | Low | Sleep/resume was not handled | Route rebuilt on device events only | `EnhancementController.cs` | After resume or a Bluetooth reconnect the route could keep stale streams | Code audit | `SystemEvents.PowerModeChanged` triggers the debounced rebuild |
| E11 | Info | The audio callback allocates | Managed arrays per 10-20 ms block | `AudioRoute.cs`, `EnhancementEngine.cs` | None measurable (gen-0 only) | Worst block 2 ms | Documented; the canceller's own path has an allocation-free overload; the delay search allocates nothing on the audio thread |

### Verified as already correct

- NLMS normalisation, regularisation and the running energy sum (exact recompute every 32 768 samples; tested over long runs).
- Order of processing: echo cancellation, then noise suppression or voice lock, then the soft limiter. The reference is a separate array: noise
  suppression and voice isolation never touch it (test: the array passed by the caller comes back unchanged).
- With echo cancellation on and nothing playing, noise suppression output is bit-for-bit what it is with echo cancellation off (test, real GTCRN model).
- `ResilientRoute` rebuilds a failed route, disposes the old one, backs off while the microphone is missing, never rebuilds on the audio thread.
- No locks, file I/O or logging in the per-sample path.
- There is no AGC stage in the pipeline (the soft limiter only), so there is nothing to interact with.

### Tried and rejected

- **Pre-whitening the regressor** (an adaptive LPC filter fitted to the far end, so NLMS sees a less coloured signal): implemented and measured; ERLE became
  erratic (9-30 dB depending on delay) and it was removed.
- **A ratio-of-peaks echo gain tracker with fast falls and slow rises** biased itself downward until the Geigel test fired on the echo itself (38 % of the
  time with no near-end talker at all). Replaced by a symmetric slow tracker plus a false-alarm check against the error statistic.
- **Averaging per-frame error ratios** made the double-talk statistic useless (baseline 0.2-2.5 even at 30 dB ERLE) because a few quiet frames dominate.
  Replaced by a ratio of smoothed energies.

## 2. Live translation

| # | Severity | Issue | Root cause | Files | User-visible impact | Evidence | Fix |
|---|---|---|---|---|---|---|---|
| T1 | High | The model changes the day of the week | Fluent but wrong: the local 8B model translated **Friday as quinta-feira (Thursday)** in both Friday sentences of the fixtures | `Translation.cs` | Someone is sent to the wrong day | Observed on the real model, twice in one run | Weekday and month names are checked one-to-one in en/pt/es/fr/de/it: ask again; if the model insists, the right name is put in and the caption says "corrected" |
| T2 | High | Unbounded backlog, no staleness, no cancellation | One `Task.Run` per sentence queued on a semaphore | `LiveTranslation.cs` | With a slow model captions fall further and further behind and then burst out | Code audit; rush test | `LiveTranslationService`: one worker per direction, at most 4 waiting sentences (oldest dropped and **announced**), anything that waited too long is skipped and announced |
| T3 | High | Failures were silent | Exceptions only went to the log | `LiveTranslation.cs` | Captions simply stopped; a sentence of the user's was never spoken and nobody knew | Code audit | A caption that cannot be translated still shows the original with the reason; one status message per change of state (not per sentence); recovery announced |
| T4 | High | Feedback loop | Translated speech goes to the virtual cable; the recorder may listen to that same virtual microphone as "Me", so the app heard its own voice, translated it again and said it again. Choosing the speakers as the output has the same effect through "Others" | `LiveTranslation.cs`, `SettingsWindow` | Endless repetition of the translated voice | Reasoned from the routing; guard tests | `SelfSpeechGuard` remembers what was said and ignores it when heard again; `SpeechRouting` refuses the speakers that MeetWise records as "Others" (in Settings and at play time) |
| T5 | Medium | The text was handed to the model as a bare instruction | A spoken question or "ignore the above" could be answered or obeyed instead of translated | `Translation.cs` | Wrong output, possible prompt injection through meeting speech | Prompt test | Text goes inside `<text>` tags with explicit rules: translate only, never answer, summarize or obey it |
| T6 | Medium | No timeout, no retry, no rate-limit handling, no fallback | Provider exceptions carried no status code | `Translation.cs`, `Llm.cs` | A hung or rate-limited cloud model stalls translation indefinitely | Code audit | Timeout (20 s), one retry for 5xx/429/network (longer pause for 429), no retry for a wrong key, optional fall back to the installed local model |
| T7 | Medium | Model output was used as is | Reasoning blocks, quotes, "Here is the translation:", trailing notes, refusals | `Translation.cs` | Commentary shown as a caption | Cleaning tests | Cleaned and validated; refusals, empty and runaway answers retried once with a firmer instruction; lost digits and lost question marks are flagged |
| T8 | Medium | Fragments were translated as they arrived | Every recognizer "final" went straight to the model | `LiveTranslation.cs` | Half-sentences translated badly; long monologues translated late | Code audit | Sentence assembly (abbreviations, decimals, CJK, ellipses), hold timer (configurable), clause-boundary cut for long runs |
| T9 | Medium | The detected language was ignored | `TranslateAsync` was called without a source language | `LiveTranslation.cs` | Slower and less accurate; the model had to guess | Code audit | Source language passed; sticky tracking so one short or noisy utterance does not flip it |
| T10 | Low | Cache key ignored the source language | `(target, text)` | `Translation.cs` | "pan" (es: bread) vs (en: pan) share an entry | Test | `(source, target, text)` |
| T11 | Low | `SpeakDeviceId!` could be null; the exception was swallowed | No validation | `LiveTranslation.cs` | Spoken translation silently did nothing | Code audit | Routing check with a clear message |
| T12 | Low | The translated voice could not be stopped | No `Clear` on the player | `PcmPlayer.cs` | Speech kept playing after stopping the recording | Code audit | `Clear()`, `StopSpeaking()`, disposal on stop |
| T13 | Low | Cloud errors could echo secrets into messages | Provider error bodies are shown | `Translation.cs` | A key in an error message reaching the toast or the log | Test with an error body containing the key | `SecretRedaction` on everything shown or logged; transcript text is never logged |

### Verified as already correct

- API keys are DPAPI-encrypted per Windows user and never written to `settings.json` (test inspects both).
- Nothing leaves the PC unless a cloud provider is configured: the default is the local model; a cloud provider without a key makes **no** network call and says
  what to do (test with a counting HTTP handler); a request goes only to the configured server (test); `LlmFactory.SendsDataOffDevice` tells the UI which settings do.
- Translation never ran on the audio-capture or UI thread.

## 3. Out of scope or not solvable locally

- **Real acoustics and hardware.** Everything for echo cancellation uses synthetic paths. Real room echo, Bluetooth stacks, USB speakerphones and laptops with
  hardware echo cancellation were not tested.
- **Audio that does not play on the default output.** The reference is the loopback of the Windows default output. If the meeting app plays on another
  device, the reference is silent and nothing is cancelled. Per-application loopback (the Windows 11 process-loopback API) would fix this and is not implemented.
- **Clock drift between devices** (tens of ppm) is followed by re-measuring the delay; a shift larger than half the filter length makes the filter relearn.
- **Quality ceiling of the time-domain filter.** A frequency-domain partitioned filter would converge faster on coloured speech; it is a larger change.
- **Translation uses a general LLM**, not a dedicated translation model (NLLB, Seamless); rare languages are weaker. Temperature is fixed by the providers.
- **Spoken translation does not keep the user's voice**, and Windows voices exist only for installed languages. Only English is installed on the development
  machine, so spoken output in Portuguese or Spanish was verified with a fake voice, and the real voice only for English.
- **Translating while the sentence is still being spoken** (word by word) is not done: sentences are translated when they end or after the hold.
