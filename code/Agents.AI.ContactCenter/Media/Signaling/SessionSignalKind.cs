namespace Agents.AI.ContactCenter.Media.Signaling;

public enum SessionSignalKind
{
    /// <summary>A DTMF tone (value = tone character, e.g. "5", "#").</summary>
    Dtmf,

    /// <summary>Request to place the channel on hold.</summary>
    Hold,

    /// <summary>Request to resume from hold.</summary>
    Resume,

    /// <summary>Request to transfer to another destination (value = target).</summary>
    Transfer,

    /// <summary>Mute toggle (value = "true" | "false").</summary>
    Mute,

    /// <summary>Stop audio playback (value = "true" | "false").</summary>
    StopAudio,

    /// <summary>Custom application-defined signal.</summary>
    Custom
}
