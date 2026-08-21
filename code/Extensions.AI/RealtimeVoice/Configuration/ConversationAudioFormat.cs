namespace Extensions.AI.RealtimeVoice.Configuration;


/// <summary>Represents audio format configuration.</summary>
public sealed class ConversationAudioFormat
{
    /// <summary>Gets or sets the audio encoding (e.g., "pcm16", "g711_ulaw").</summary>
    public required string Encoding { get; init; }

    /// <summary>Gets or sets the sample rate in Hz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Gets or sets the number of channels.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Common PCM 16-bit format at 24kHz mono.</summary>
    public static ConversationAudioFormat Pcm16_24kHz { get; } = new()
    {
        Encoding = "pcm16",
        SampleRate = 24000,
        Channels = 1
    };
}

