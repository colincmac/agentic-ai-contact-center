namespace Extensions.AI.RealtimeVoice.Configuration;

/// <summary>Configuration for input transcription.</summary>
public sealed class RealtimeTranscriptionOptions
{
    /// <summary>Gets or sets the transcription model.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the language for transcription.</summary>
    public string? Language { get; set; }
}

