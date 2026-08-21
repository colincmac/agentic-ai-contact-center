namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Runs lightweight paralingual analysis on raw audio frames,
/// producing structured signals (emotion, speech rate, stress)
/// without requiring transcription.
/// </summary>
public interface IAudioAnalysisPipeline
{
    /// <summary>
    /// Analyzes a buffered window of PCM audio and returns paralingual features.
    /// </summary>
    /// <param name="audioWindow">Buffered PCM audio for the analysis window.</param>
    /// <param name="sampleRateHz">Contextual information for the analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// </summary>
    Task<AudioAnalysisResult?> AnalyzeAsync(
        ReadOnlyMemory<byte> audioWindow,
        int sampleRateHz = 16_000,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregated result from one or more paralingual analyzers.
/// </summary>
public sealed class AudioAnalysisResult
{
    public EmotionSignal? Emotion { get; init; }
    public double? SpeechRate { get; init; }
    public double? StressLevel { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>
/// A discrete emotion signal extracted from audio features (pitch, energy, cadence).
/// </summary>
public sealed class EmotionSignal
{
    /// <summary>Detected emotion label (e.g., "angry", "happy", "neutral", "frustrated").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Valence score mapped to the same scale as text sentiment
    /// (−1.0 = strongly negative, +1.0 = strongly positive).
    /// Enables direct comparison with text sentiment for divergence calculation.
    /// </summary>
    public required double ValenceScore { get; init; }

    /// <summary>Confidence score for this detection (0.0–1.0).</summary>
    public required double Confidence { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Session-level context passed to analyzers so they can adapt
/// to the conversation's audio distribution (telephony vs. high-quality, etc.).
/// </summary>
public sealed class AudioAnalysisContext
{
    public string? SessionId { get; init; }
    public AudioQualityTier? Quality { get; init; }
    public double? EstimatedSnr { get; init; }
    public int SampleRateHz { get; init; } = 16_000;
    public IReadOnlyDictionary<string, object?>? ConversationState { get; init; }
}


public enum AudioQualityTier
{
    Telephony8kHz,
    StandardVoIP,
    HighQuality,
    Unknown
}
