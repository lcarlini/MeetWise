namespace MeetWise.Audio;

public enum SpeechRoutingProblem
{
    None,
    /// <summary>No output device was chosen for the translated voice.</summary>
    NoDevice,
    /// <summary>The chosen device is not connected any more.</summary>
    DeviceMissing,
    /// <summary>The chosen device is the one meeting audio plays on, which MeetWise records as "Others": it would hear (and translate) its own voice.</summary>
    SameAsSystemOutput
}

/// <summary>Decides whether the translated voice may be played on the chosen device. Pure, so it can be tested without any audio hardware.</summary>
public static class SpeechRouting
{
    /// <param name="chosenId">The output device picked in Settings.</param>
    /// <param name="availableRenderIds">Ids of the output devices that exist right now.</param>
    /// <param name="defaultRenderId">The Windows default output: its loopback is the "Others" channel.</param>
    public static (SpeechRoutingProblem Problem, string Message) Check(string? chosenId, IReadOnlyCollection<string> availableRenderIds, string? defaultRenderId)
    {
        if (string.IsNullOrEmpty(chosenId)) return (SpeechRoutingProblem.NoDevice, "Choose the output device for the translated voice in Settings.");
        if (!availableRenderIds.Contains(chosenId)) return (SpeechRoutingProblem.DeviceMissing, "The output device chosen for the translated voice is not connected.");
        if (defaultRenderId is not null && chosenId == defaultRenderId)
            return (SpeechRoutingProblem.SameAsSystemOutput,
                "The translated voice would play on the speakers that MeetWise records as \"Others\", so it would be heard and translated again. Choose the MeetWise Mic Feed or another device.");
        return (SpeechRoutingProblem.None, "");
    }
}
