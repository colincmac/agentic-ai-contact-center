namespace Agents.AI.ContactCenter.Authorization.Biometrics;

public interface IVoiceBiometricEvaluator
{
    Task<VoiceAnomalyAnalysis> AnalyzeVoiceAnomaliesAsync(string participantId, ReadOnlyMemory<byte> audioSample, CancellationToken cancellationToken = default);
    bool DeleteProfile(string participantId);
    Task<VoiceEnrollmentResult> EnrollVoiceAsync(string participantId, ReadOnlyMemory<byte> audioSample, CancellationToken cancellationToken = default);
    VoiceBiometricProfile? GetProfile(string participantId);
    Task<VoiceVerificationResult> VerifyVoiceAsync(string participantId, ReadOnlyMemory<byte> audioSample, CancellationToken cancellationToken = default);
}
