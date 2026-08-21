using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

/// <summary>
/// Requires fraud risk assessment to be below threshold before executing a tool
/// </summary>
public sealed class RequiresFraudCheckRequirement : IToolApprovalRequirement
{
    public RequiresFraudCheckRequirement(double maxRiskScore = 50.0)
    {
        MaxRiskScore = maxRiskScore;
    }

    public double MaxRiskScore { get; }

    public AIContent? OnFailureResponse => new TextContent(
        "This action cannot be completed at this time due to security concerns. Please contact support.");
}
