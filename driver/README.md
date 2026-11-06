# MeetWise virtual audio driver (not built yet)

The goal is two Windows audio endpoints that work in every meeting app without a bot:

- **MeetWise Microphone** (capture): meeting apps record from it and receive the cleaned voice.
- **MeetWise Speaker** (render): meeting apps play into it; MeetWise cleans the other participants' audio and forwards it to the real speakers.

## Why it is not here

A virtual endpoint is a kernel-mode audio miniport driver. Building and shipping one needs:

1. The Windows Driver Kit (WDK) and a matching Visual Studio workload. Not installed on the development machine.
2. An EV code-signing certificate and a Microsoft Partner Center account for attestation signing. Windows 11 with Secure Boot refuses unsigned drivers.
3. Testing on VMs/test-signing machines before signing.

Writing driver code that cannot be compiled or tested here would not be trustworthy, so none is included.

## Plan

1. Start from Microsoft's open-source **SysVAD** sample (`microsoft/Windows-driver-samples`, `audio/sysvad`), which already implements a virtual speaker and microphone with a loopback.
2. Replace the loopback with a shared-memory ring buffer (one per direction) that `MeetWise.Audio` reads and writes. The app side is ready: `AudioRoute` takes any capture/render device pair, so the driver's endpoints plug in without code changes.
3. Sign through attestation signing; install with `pnputil /add-driver MeetWise.inf /install` from an elevated step in the installer.

## Until then

Install the free **VB-CABLE** (vb-audio.com/Cable). Choose "CABLE Input" as the output in MeetWise's *Audio enhancement* panel and "CABLE Output" as the microphone in Zoom, Teams or Meet. For cleaning incoming audio, route the meeting app's output to a second cable and capture that cable as the route's input.

The bot-free *recording* path does not need any driver: it uses WASAPI loopback of the default output and your microphone.
