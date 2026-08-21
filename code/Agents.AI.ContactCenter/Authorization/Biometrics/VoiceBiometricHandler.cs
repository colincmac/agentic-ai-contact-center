using System.Diagnostics;
using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

/// <summary>
/// Handles voice biometric verification requirements for gating sensitive tool actions.
/// This handler checks if the caller has been verified through voice biometrics before
/// allowing access to protected operations.
/// </summary>
public sealed class VoiceBiometricHandler : ToolApprovalHandler<RequiresVoiceBiometricRequirement>
{
    private readonly ILogger<VoiceBiometricHandler> _logger;
    private readonly IVoiceBiometricEvaluator _biometricEvaluator;

    /// <summary>
    /// Activity source for telemetry.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Agents.AI.Biometrics.Authorization", "1.0.0");

    public VoiceBiometricHandler(
        IVoiceBiometricEvaluator biometricEvaluator,
        ILogger<VoiceBiometricHandler>? logger = null)
    {
        _biometricEvaluator = biometricEvaluator;
        _logger = logger ?? NullLogger<VoiceBiometricHandler>.Instance;
    }

    protected override async Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresVoiceBiometricRequirement requirement)
    {
        using var activity = ActivitySource.StartActivity("VoiceBiometricAuthorization");
        activity?.SetTag("tool.name", context.Tool.Name);
        activity?.SetTag("requirement.threshold", requirement.ConfidenceThreshold);

        var participantId = GetParticipantId(context);
        activity?.SetTag("participant.id", participantId);

        // Check if voice biometric verification result is already provided
        if (TryGetVerificationResult(context, out var verificationResult))
        {
            if (verificationResult.Success && verificationResult.IsMatch &&
                verificationResult.ConfidenceScore >= requirement.ConfidenceThreshold)
            {
                _logger.LogInformation(
                    "Voice biometric verification passed for participant {ParticipantId} with confidence {Confidence:P2} (threshold: {Threshold:P2})",
                    participantId, verificationResult.ConfidenceScore, requirement.ConfidenceThreshold);

                activity?.SetTag("authorization.result", "success");
                activity?.SetTag("verification.confidence", verificationResult.ConfidenceScore);

                context.Succeed(requirement);
                return;
            }

            if (verificationResult.Success && verificationResult.IsMatch)
            {
                _logger.LogWarning(
                    "Voice biometric verification confidence {Confidence:P2} below threshold {Threshold:P2} for participant {ParticipantId}",
                    verificationResult.ConfidenceScore, requirement.ConfidenceThreshold, participantId);

                activity?.SetTag("authorization.result", "low_confidence");
                activity?.SetTag("verification.confidence", verificationResult.ConfidenceScore);
            }
            else
            {
                _logger.LogWarning(
                    "Voice biometric verification did not match for participant {ParticipantId}",
                    participantId);

                activity?.SetTag("authorization.result", "no_match");
            }
        }
        else
        {
            _logger.LogInformation(
                "Voice biometric verification required for participant {ParticipantId} - no verification result provided",
                participantId);

            activity?.SetTag("authorization.result", "verification_required");
        }

        context.Fail(requirement);
        await Task.CompletedTask;
    }

    private static bool TryGetVerificationResult(ToolApprovalContext context, out VoiceVerificationResult result)
    {
        // Check for explicit verification result in arguments
        if (context.Arguments.TryGetValue("voiceVerificationResult", out var resultObj) &&
            resultObj is VoiceVerificationResult verificationResult)
        {
            result = verificationResult;
            return true;
        }

        // Check for legacy voiceVerified flag with optional confidence
        if (context.Arguments.TryGetValue("voiceVerified", out var verified) &&
            verified is bool isVerified && isVerified)
        {
            var confidence = 1.0;
            if (context.Arguments.TryGetValue("voiceConfidence", out var conf) && conf is double confValue)
            {
                confidence = confValue;
            }

            result = new VoiceVerificationResult
            {
                Success = true,
                IsMatch = true,
                ConfidenceScore = confidence,
                VerifiedAt = DateTimeOffset.UtcNow
            };
            return true;
        }

        result = new VoiceVerificationResult();
        return false;
    }

    private static string GetParticipantId(ToolApprovalContext context)
    {
        return context.Arguments.TryGetValue("participantId", out var participantId) && participantId is string pid
            ? pid
            : "default";
    }
}
