namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Configuration options for the voice biometrics API service.
/// </summary>
public sealed class VoiceBiometricsApiOptions
{
    /// <summary>
    /// Configuration section name for binding from appsettings.
    /// </summary>
    public const string SectionName = "VoiceBiometricsOptions";

    /// <summary>
    /// The gRPC endpoint URL for the biometrics service (e.g., "https://localhost:50051").
    /// </summary>
    public string? Endpoint { get; set; }

    public string? ConnectionStringName { get; set; }

    /// <summary>
    /// Timeout for gRPC calls in seconds. Default is 30 seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Confidence threshold for voice verification (0.0 to 1.0).
    /// Verifications below this threshold will be rejected.
    /// </summary>
    public double VerificationThreshold { get; set; } = 0.25;

    /// <summary>
    /// Minimum audio duration in seconds required for enrollment/verification.
    /// </summary>
    public double MinAudioDurationSeconds { get; set; } = 1.0;

    /// <summary>
    /// Maximum audio duration in seconds allowed for enrollment/verification.
    /// </summary>
    public double MaxAudioDurationSeconds { get; set; } = 30.0;

    /// <summary>
    /// Whether to enable the biometrics API integration.
    /// When false, falls back to the stub evaluator.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to allow insecure (non-TLS) connections.
    /// Only set to true for development environments.
    /// </summary>
    public bool AllowInsecureConnection { get; set; } = false;

    /// <summary>
    /// Number of retry attempts for failed API calls.
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Initial delay in milliseconds between retry attempts (with exponential backoff).
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 500;
}

