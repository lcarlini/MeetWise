using System.Runtime.InteropServices;

namespace MeetWise.Audio;

/// <summary>Turns Windows audio error codes into sentences a person can act on (the raw text is "Class not registered (0x80040154)").</summary>
public static class AudioErrors
{
    public static string Describe(Exception ex)
    {
        if (ex is TimeoutException) return ex.Message;
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is not COMException c) { if (e.InnerException is null) break; continue; }
            switch ((uint)c.HResult)
            {
                case 0x80040154: // REGDB_E_CLASSNOTREG: the audio client cannot be created, typically after the Windows Audio service crashed
                case 0x88890004: // AUDCLNT_E_DEVICE_INVALIDATED
                case 0x80070490: // no such endpoint (no default device)
                    return "Windows audio is not responding. Unplug and plug the microphone in again, or restart the Windows Audio service (or sign out and in), then try again.";
                case 0x8889000A: // AUDCLNT_E_DEVICE_IN_USE
                    return "The audio device is in use by another program in exclusive mode. Close that program or turn off exclusive mode in the device's properties, then try again.";
                case 0x800706CC: // endpoint is a duplicate
                    return "Windows reports this audio device twice, which usually means a virtual audio driver (such as NVIDIA Broadcast) is stuck. Restart that program, or unplug and plug the device in again.";
            }
        }
        return ex.Message;
    }
}
