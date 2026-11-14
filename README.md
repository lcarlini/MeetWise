<p align="center"><img src="docs/img/icon.png" width="96" alt="MeetWise icon"></p>

# MeetWise

A clean microphone for Windows 11, in the style of Krisp and NVIDIA Broadcast. MeetWise creates a **MeetWise Microphone** that every meeting app can choose. It contains only your voice: background noise, echo and (optionally) other people's voices are removed on your own PC, in real time.

Phase 2, **Accent Assist**, is real-time pronunciation softening for Brazilian speakers talking to US listeners. The real-time pipeline is built: a GPU worker, 300 ms fixed delay and automatic fallback to your own voice. The current model does not yet keep your voice well enough (speaker similarity 0.62 against a 0.75 target), so it ships **off and marked experimental**. See [docs/PLAN.md](docs/PLAN.md).

Measured quality: [docs/QUALITY-REPORT.md](docs/QUALITY-REPORT.md).

## Use it

1. Open MeetWise and click **Create MeetWise Microphone** once. Windows asks for administrator permission, and the device may only appear after a restart.
2. Click **Turn on**. MeetWise remembers this: the microphone stays on in the background and starts with Windows.
3. In Zoom, Teams, Meet or Discord, choose **MeetWise Microphone** as the microphone. Alternatively, click **Make it the Windows default**. In Zoom, set Zoom's own background-noise suppression to **Low**, because MeetWise already removed the noise.
4. Click **Test as Zoom hears me**. It records from the MeetWise Microphone itself, which is exactly what the other people receive, and plays it back next to your raw microphone.

Closing the window does **not** stop the microphone. The audio runs in a separate background process (`MeetWise.App.exe --audio-host`) with a tray icon. Only **Quit** in the tray stops it.

## Who cleans the microphone: MeetWise or NVIDIA Broadcast

The Clean-up card has a choice between **MeetWise** and **NVIDIA Broadcast**.

With **NVIDIA Broadcast**:
- Broadcast removes the noise and the room echo. Turn on Noise Removal and Room Echo Removal in its app.
- MeetWise reads "Microphone (NVIDIA Broadcast)". Its own noise removal and background-voice removal are switched off.
- MeetWise only adds Accent Assist, plus speaker-echo cancellation and automatic volume if you switch them on. Broadcast's Room Echo Removal removes reverberation; it does not cancel the other people's voices coming back through your speakers.
- In Zoom/Teams, keep choosing **MeetWise Microphone**, because MeetWise adds the accent stage.

If the Broadcast microphone is missing or stops responding, MeetWise cleans the microphone itself until it is back, so the call is never silent. Both apps share the GPU. Accent Assist needs about 0.7 GB and only starts with at least 1.5 GB of graphics memory free.

## What it removes

| Switch | What happens |
|---|---|
| Remove background noise | DPDFNet, from the DeepFilterNet family. **Max** runs at 48 kHz full band, about 34 % of one CPU core, with 40 ms look-ahead. **Balanced** and **Light** run at 16 kHz. It removes dogs, birds, keyboard and mouse clicks, fans, traffic, TV and room noise. **Strength** limits how far noise is pushed down: 100 % removes everything it finds, 50 % keeps about −23 dB of room tone. |
| Remove echo | GCC-PHAT delay alignment and an NLMS filter with double-talk protection, followed by a residual suppressor. The reference is what plays on the **Communications** default output, which is where Zoom and Teams play. |
| Remove background voices | VoxLock target-speaker extraction in full precision (fp32), using the voice you train in about a minute. The noise remover still runs after it. |
| Automatic volume | A slow AGC that only measures while you speak. It aims at −20 dBFS and never raises noise in pauses. |
| Always on | An 80 Hz high-pass filter (thumps, hum) and a 1.5 ms look-ahead limiter that keeps peaks below −1 dBFS without distortion. |

If the PC cannot keep up, the chain steps down by itself: Max, then Balanced, then Light, and finally voice lock is paused for the session. If a model ever fails on a block, that block passes through unprocessed. The meeting never hears silence.

## Why it was silent in Zoom/Teams (fixed)

The old version could work in its own test and still give Zoom silence. There were five independent reasons:

1. The test never went through the virtual microphone. It processed a recording offline. The new test records from the MeetWise Microphone itself.
2. The hidden "Start cleanup" button sent the cleaned voice to the **first output in a list**, usually the speakers, instead of the MeetWise Mic Feed. That list is gone, and the output is always the feed.
3. "Always on" was off by default, so nothing fed the cable. It is now on, and it is switched on once for existing settings.
4. Closing the window stopped the microphone. The audio now lives in the background process.
5. On the development PC, both sides of the cable were **disabled** in Windows, so Zoom/Teams did not even list them. MeetWise now detects this, and **Repair** switches them back on.

Two more failure modes found while testing on the real machine are handled without freezing:

- **NVIDIA Broadcast devices hang.** Opening them can block for ever. The echo reference now gets 2 s, the microphone 3 s and the whole route 10 s; a device that hangs is set aside for a minute and the route retries.
- **The Windows audio service itself hung and was restarted by Windows.** This happened on 2026-11-07 and twice on 2026-11-13, each time shortly after the virtual cable's endpoints were changed (renamed or re-enabled) while NVIDIA Broadcast was running. MeetWise therefore no longer changes endpoints by itself; Repair does it on request, with a warning.

## The virtual microphone driver

Secure Boot is on, and Windows only loads audio drivers that Microsoft has signed (attestation signing, which needs an EV certificate). A driver written from scratch and left unsigned cannot load. The MeetWise Microphone is therefore VB-Audio's signed VB-CABLE, downloaded unmodified on demand and renamed to "MeetWise Microphone" / "MeetWise Mic Feed". To Zoom it is indistinguishable from a driver of our own. All processing is MeetWise's. See [driver/README.md](driver/README.md) for the path to a MeetWise-signed driver.

## Checks

```
MeetWise.App.exe --selftest
```

The self-test checks devices, the cable state and formats, every noise model and its measured look-ahead, and the full chain's speed. When the background microphone runs, it also performs the **end-to-end check**: a 1 s chirp is mixed into the output and recorded back from the MeetWise Microphone, which is the proof that Zoom/Teams receive audio. The report is also written to `%LocalAppData%\MeetWise\selftest.txt`. The window's **Check connection** button runs the same check.

Unit and integration tests run with `dotnet test tests/MeetWise.Tests -c Release`.

## Build

- `.NET 10`, WPF; `dotnet build`
- `publish.ps1` publishes a self-contained build to `dist\MeetWise`
- Models download on first use to `%LocalAppData%\MeetWise\models` and are SHA-256 verified. They come from the k2-fsa sherpa-onnx releases: DPDFNet 16/48 kHz, GTCRN, VoxLock fp32 and int8, Silero VAD and CAM++.

## Not done yet

- **Accent Assist (phase 2):** the infrastructure is done. A model that keeps the user's own voice needs about 20 min of their recorded speech and a vocoder fine-tuned on it; see [docs/PLAN.md](docs/PLAN.md).
- **Background voices at equal loudness:** at a signal-to-interference ratio of 0–5 dB, voice isolation lowers the word error rate only from 31 % to 27 % (docs/QUALITY-REPORT.md). It works well when the other voice is quieter.
- **Running voice lock on the GPU:** sherpa-onnx publishes a CUDA 13 build, but it also needs cuDNN 9, which is not yet part of the GPU pack.
- **A direct comparison with Krisp or NVIDIA Broadcast on the same files.** Krisp is not installed here, and Broadcast needs working live audio. `accent/bench_clean.py` already produces the MeetWise side: DNSMOS, WER and SI-SDR for 12 noise types, background voices and echo.
- **A real Zoom/Teams call with another person** cannot be tested automatically. The end-to-end chirp check is the technical equivalent.
