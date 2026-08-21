using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

/// <summary>
/// Requires voice biometric verification before executing a tool
/// </summary>
public sealed class RequiresVoiceBiometricRequirement : IToolApprovalRequirement
{
    public RequiresVoiceBiometricRequirement(double confidenceThreshold = 0.85)
    {
        ConfidenceThreshold = confidenceThreshold;
    }

    public double ConfidenceThreshold { get; }

    public AIContent? OnFailureResponse => new TextContent(
        "This action requires voice biometric verification. Please speak clearly for voice verification.");
}
