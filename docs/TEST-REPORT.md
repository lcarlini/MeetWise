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
