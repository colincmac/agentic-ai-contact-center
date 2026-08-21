namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Structured payload for cross-signal analysis results published to the
/// <see cref="HubSessionEventBus"/> as <see cref="HubSessionEventKind.AgentInsight"/> events.
/// <para>
/// Combines audio-derived paralingual features (emotion, stress, speech rate)
/// with text-derived sentiment to detect divergence — e.g., polite words
/// spoken in an angry tone.
/// </para>
/// </summary>
public sealed record ConversationSignalAnalysis
{
    /// <summary>Audio-derived emotion signal from paralingual analysis.</summary>
    public EmotionSignal? AudioEmotion { get; init; }

    /// <summary>Words-per-minute estimated from the audio stream.</summary>
    public double? SpeechRate { get; init; }

    /// <summary>Vocal stress level (0.0 = calm, 1.0 = extreme stress).</summary>
    public double? StressLevel { get; init; }

    /// <summary>Text-derived sentiment score (−1.0 negative … +1.0 positive).</summary>
    public double? TextSentiment { get; init; }

    /// <summary>
    /// Absolute divergence between text sentiment and audio emotion.
    /// Values above <see cref="DivergenceThreshold"/> indicate the text
    /// and voice signals disagree meaningfully (e.g., sarcasm, suppressed frustration).
    /// </summary>
    public double? Divergence { get; init; }

    /// <summary>Whether the divergence exceeds the configured threshold.</summary>
    public bool IsDivergent { get; init; }

    /// <summary>Human-readable explanation when divergent.</summary>
    public string? DivergenceDescription { get; init; }

    /// <summary>Timestamp of the analysis window.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The analysis window duration these results cover.
    /// </summary>
    public TimeSpan WindowDuration { get; init; }

    /// <summary>Default threshold above which signals are considered divergent.</summary>
    public const double DivergenceThreshold = 0.4;
}

