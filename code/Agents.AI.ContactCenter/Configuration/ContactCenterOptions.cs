using System.Text.Json.Serialization;
using Agents.AI.Realtime;

namespace Agents.AI.ContactCenter.Configuration;

public class ContactCenterOptions
{
    public const string SectionName = "ContactCenter";

    [JsonIgnore]
    public AgentFunctionInvocationMiddleware? AgentFunctionInvocationMiddleware { get; set; } = null;

    public string? RealtimeAgentServiceKey { get; set; }

    /// <summary>
    /// Enable Entra identity verification
    /// </summary>
    public bool EnableEntraVerification { get; set; } = true;

    /// <summary>
    /// Enable voice approval middleware
    /// </summary>
    public bool EnableVoiceApproval { get; set; } = true;

    /// <summary>
    /// Fraud detection options
    /// </summary>
    public FraudDetectionOptions? FraudDetection { get; set; } = new FraudDetectionOptions();
    /// <summary>
    /// Voice biometric options
    /// </summary>
    public VoiceBiometricOptions? VoiceBiometrics { get; set; } = new VoiceBiometricOptions();

    /// <summary>
    /// Agent tier degradation options for capacity-aware graceful degradation.
    /// When configured, new sessions are assigned to the best available tier
    /// based on current load.
    /// </summary>
    public AgentTierOptions? AgentTiers { get; set; }
}
public sealed class FraudDetectionOptions
{
    public bool Enabled { get; set; } = false;
    public double HighRiskThreshold { get; set; } = 50.0;
    public double CriticalRiskThreshold { get; set; } = 75.0;
    public bool EnableRealTimeAlerts { get; set; } = true;
    public TimeSpan RapidRequestWindow { get; set; } = TimeSpan.FromSeconds(1);
}
public sealed class VoiceBiometricOptions
{
    public bool Enabled { get; set; } = false;

    public int MinimumEnrollmentSamples { get; set; } = 3;
    public double VerificationThreshold { get; set; } = 0.85; // 85% confidence
    public double AnomalyThreshold { get; set; } = 0.6; // 60% anomaly score
    public bool EnableLivenessDetection { get; set; } = true;
    public bool EnableSyntheticVoiceDetection { get; set; } = true;
}
