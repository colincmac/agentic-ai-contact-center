namespace Agents.AI.ContactCenter.Authorization.Biometrics;

public sealed class VoiceBiometricProfile
{
    public string ParticipantId { get; set; } = string.Empty;
    public bool IsEnrolled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastEnrolledAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public int EnrollmentSamples { get; set; }
    public int VerificationAttempts { get; set; }
    public int SuccessfulVerifications { get; set; }
}

public sealed class VoiceEnrollmentResult
{
    public bool Success { get; set; }
    public bool IsComplete { get; set; }
    public int SamplesCollected { get; set; }
    public int SamplesRequired { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class VoiceVerificationResult
{
    public bool Success { get; set; }
    public bool IsMatch { get; set; }
    public double ConfidenceScore { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class VoiceAnomalyAnalysis
{
    public string ParticipantId { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; }
    public bool IsSyntheticVoiceDetected { get; set; }
    public StressLevel StressLevel { get; set; }
    public double BackgroundNoiseLevel { get; set; }
    public double AnomalyScore { get; set; }
    public List<string> DetectedAnomalies { get; set; } = new();
}

public enum StressLevel
{
    VeryLow,
    Low,
    Normal,
    Elevated,
    High,
    VeryHigh
}
