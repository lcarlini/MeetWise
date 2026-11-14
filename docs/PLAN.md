# MeetWise plan: clean microphone, then Accent Assist

The target user is a Brazilian Portuguese speaker who speaks English with US colleagues. The machine is a Ryzen 9 laptop (16 threads, 32 GB) with an RTX 4070 Laptop GPU (8 GB).

## Phase 1: clean microphone (built)

The README describes what exists. It lists the chain, the fixes for "silent in Zoom/Teams", and how the app survives hung devices.

These parts of phase 1 are still open:

| Item | What to do | How to know it is done |
|---|---|---|
| Quality bench (`tools/MeetWise.Bench`) | Build a corpus: the user's voice (recorded once) mixed with each noise type (dog, birds, keyboard, TV, background talk, shouting, room) at 0, 5, 10 and 20 dB SNR, plus simulated echo (`EchoSim`). Process it through MeetWise, and through NVIDIA Broadcast via a cable loop. | DNSMOS P.835 OVRL ≥ Broadcast's for every noise type; word error rate (streaming zipformer, or Whisper on the GPU) no more than 2 points worse than the clean voice. |
| Background voices | Measure VoxLock fp32 against int8 on the user's own voice. If neither passes, add a near-field gate as a second layer: the user's voice is louder and more direct than voices farther away. | WER change ≤ 2 points; an interfering talker ≥ 20 dB down. If it fails, the feature stays "beta" and off by default, and the numbers are shown. |
| GPU | Run the sherpa-onnx CUDA 13 build (it needs cuDNN 9 in the GPU pack), or call ONNX Runtime GPU directly. This frees about 4 CPU cores when voice lock is on. | Voice lock at ≤ 20 % of one core, with ≤ 40 ms extra delay. |
| Neural residual echo | DTLN-aec (Microsoft AEC challenge, MIT) after the NLMS filter. | More ERLE on `EchoSim`, with no loss of the near-end voice in double talk. |

## Phase 2: Accent Assist (infrastructure built, model below the quality gates)

### What exists and works

- **Worker** (`accent/worker.py`, Python 3.13 + PyTorch 2.11 CUDA in `%LocalAppData%\MeetWise\accent\venv`): kNN accent softening. WavLM-Large layer-6 features of the live voice are pulled (by `strength`) towards their nearest neighbours in a bank of native US-English frames, then vocoded by kNN-VC's prematched HiFi-GAN. Streaming in 160 ms hops with 60 ms look-ahead: **220 ms algorithmic latency**, **29 ms median GPU time per hop** on the RTX 4070.
- **`AccentStage`** in the audio host. It has a fixed 300 ms delay, talks to the worker over 127.0.0.1 with a random token, and falls back to the original voice with 10 ms cross-fades whenever converted audio is late. In a real-time run through the app (`--process --accent --realtime`), the original voice was needed **0 % of the time**.
- **UI:** an on/off switch and a strength slider, with the live state from the host. The feature is **off by default** and marked experimental.
- **Measurement:** `accent/experiment.py` runs on the user's own recordings and reports speaker similarity (ECAPA), US-accent probability (CommonAccent) and Whisper transcript agreement.

### Measured on the user's recordings (2026-11-13)

| | Speaker similarity to the user | Words |
|---|---|---|
| Resynthesis only (strength 0) | 0.68 | intact |
| Native bank, strength 0.3–0.5 | 0.64–0.72 | "testing" → "thirsty" on an isolated word |
| Through the full app chain, strength 0.5 | 0.62 | sentence intact |

**Gate: ≥ 0.75. It fails.**

- The kNN-VC vocoder, trained on other speakers at 16 kHz, loses the user's identity even with nothing changed.
- The CommonAccent classifier has no Brazilian class and drops for any resynthesis, so it is not a usable accent measure here.

### What is needed to pass, in order

1. **The user's voice.** About 20 min of reading (the PC must be rebooted first: the Windows audio service is broken until then).
2. **A vocoder fine-tuned on that voice** (prematched HiFi-GAN training, about 1–2 h on the 4070). This is what restores identity; kNN-VC reports about 0.9 similarity for in-domain speakers.
3. **A native-pronunciation bank in the user's own voice.** Native US sentences (CMU ARCTIC bdl/rms) are re-voiced with Seed-VC, using the user's voice as the reference (`accent/build_bank.py`). Running this step was blocked by Claude Code's permission system, because it synthesises speech in the user's voice. It needs the user's explicit go-ahead.
4. Re-run `experiment.py` and only enable the feature by default when similarity is ≥ 0.75 and the WER does not get worse.

### Using NVIDIA Broadcast for the cleanup (2026-11-14)

The user can let NVIDIA Broadcast remove the noise and room echo (setting `Cleanup = NvidiaBroadcast`). MeetWise then reads Broadcast's microphone and only adds Accent Assist.

This separates the jobs, but it does **not** change the accent gates above. The identity loss was measured on clean recordings, so it comes from the vocoder, not from noise. Steps 1–4 are still required.

