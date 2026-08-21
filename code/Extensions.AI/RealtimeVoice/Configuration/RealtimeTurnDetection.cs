namespace Extensions.AI.RealtimeVoice.Configuration;

/// <summary>Configuration for turn detection.</summary>
public sealed class RealtimeTurnDetection
{
    /// <summary>Gets or sets the type of turn detection.</summary>
    public RealtimeTurnDetectionType Type { get; set; }

    /// <summary>Gets or sets the silence threshold in milliseconds.</summary>
    public int? SilenceThresholdMs { get; set; }

    /// <summary>Gets or sets the voice activity detection threshold.</summary>
    public float? VadThreshold { get; set; }

    /// <summary>Gets or sets prefix padding in milliseconds.</summary>
    public int? PrefixPaddingMs { get; set; }

    public int? SpeechDurationMs { get; set; }

    public bool EnableAutomaticResponse { get; set; } = true;

    public bool EnableResponseInterruption { get; set; } = true;

    public bool EnableAutomaticTruncation { get; set; } = true;

    /// <summary>Creates server-side voice activity detection configuration.</summary>
    public static RealtimeTurnDetection ServerVad(int? silenceThresholdMs = null) => new()
    {
        Type = RealtimeTurnDetectionType.ServerVad,
        SilenceThresholdMs = silenceThresholdMs
    };

    /// <summary>Creates disabled turn detection configuration.</summary>
    public static RealtimeTurnDetection Disabled => new()
    {
        Type = RealtimeTurnDetectionType.Disabled
    };
}

