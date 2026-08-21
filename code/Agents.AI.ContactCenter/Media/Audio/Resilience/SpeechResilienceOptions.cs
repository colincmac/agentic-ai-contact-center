namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// Tunable parameters for the Polly-based resilient speech pipeline shared by the
/// recognizer and synthesizer decorators.
/// </summary>
/// <remarks>
/// A single instance is bound from <c>AzureSpeech:Resilience</c> and used to build
/// one <c>ResiliencePipeline</c> per <c>(endpoint, operation)</c> pair. Settings are
/// applied identically across endpoints; per-endpoint state (circuit breaker) is
/// isolated by virtue of the per-endpoint pipeline.
/// </remarks>
public sealed class SpeechResilienceOptions
{
    /// <summary>
    /// Maximum time a single attempt (one endpoint, one try) is allowed to take
    /// before being cancelled with a <see cref="TimeoutException"/>. Applies to the
    /// "acquire enumerator + advance to first chunk" phase for synthesis and to
    /// the session-start phase for recognition.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Number of retry attempts against the same endpoint before moving on.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Initial backoff between retries (exponential, with jitter).</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum delay cap for retry backoff.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Failure ratio (0.0-1.0) that trips the circuit breaker for an endpoint.</summary>
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Sliding window over which <see cref="BreakerFailureRatio"/> is evaluated.</summary>
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum number of actions in the sampling window before the breaker can trip.</summary>
    public int BreakerMinimumThroughput { get; set; } = 5;

    /// <summary>How long the breaker stays open before transitioning to half-open.</summary>
    public TimeSpan BreakerDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <c>true</c> (default) the decorators walk the ordered endpoint list on
    /// fallback. When <c>false</c> the primary endpoint's per-pipeline outcome is
    /// surfaced directly to the caller.
    /// </summary>
    public bool EnableFallback { get; set; } = true;
}
