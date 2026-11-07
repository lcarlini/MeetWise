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

## What the app does today

The **MeetWise Microphone** is created with VB-Audio's signed **VB-CABLE** (vb-cable.com, donationware):

1. "Create MeetWise Microphone" downloads the unmodified official pack (about 1.3 MB, SHA-256 checked) and starts `MeetWise.App.exe --install-virtual-mic` with a Windows administrator prompt. That helper runs `VBCABLE_Setup_x64.exe -i -h`, waits for the endpoints and renames them to "MeetWise Microphone" and "MeetWise Mic Feed". Windows may need a restart before the device appears.
2. MeetWise writes your cleaned microphone into "MeetWise Mic Feed"; every app that records from "MeetWise Microphone" hears it.
3. "Make it my Windows default" switches the default microphone, and "Restore" puts the previous one back. The app refuses to make it the default unless the clean-up route is running, because apps would hear silence.

VB-CABLE's licence does not allow integrating the package into another software's installation procedure without the author's agreement, so it is downloaded on the user's request rather than bundled, and the app shows the credit the licence asks for. A driver of our own would remove that dependency; the app side already works with any device pair, so only the device detection in `VirtualMic.cs` would change.

The bot-free *recording* path does not need any driver: it uses WASAPI loopback of the default output and your microphone.
