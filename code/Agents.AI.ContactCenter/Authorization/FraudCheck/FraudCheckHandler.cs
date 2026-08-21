using Agents.AI.Extensions.ToolApproval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

/// <summary>
/// Handles fraud check requirements
/// </summary>
public sealed class FraudCheckHandler : ToolApprovalHandler<RequiresFraudCheckRequirement>
{
    private readonly ILogger<FraudCheckHandler> _logger;
    private readonly FraudDetectionMonitor _fraudMonitor;

    public FraudCheckHandler(
        FraudDetectionMonitor fraudMonitor,
        ILogger<FraudCheckHandler>? logger = null)
    {
        _fraudMonitor = fraudMonitor;
        _logger = logger ?? NullLogger<FraudCheckHandler>.Instance;
    }

    protected override async Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresFraudCheckRequirement requirement)
    {
        var sessionId = GetSessionId(context);

        // Get current fraud assessment
        var assessment = _fraudMonitor.GetAssessment(sessionId);

        if (assessment is null || assessment.RiskScore <= requirement.MaxRiskScore)
        {
            _logger.LogInformation(
                "Fraud check passed for session {SessionId} with risk score {RiskScore}",
                sessionId, assessment?.RiskScore ?? 0);

            context.Succeed(requirement);
        }
        else
        {
            _logger.LogWarning(
                "Fraud check failed for session {SessionId} with risk score {RiskScore} (max: {MaxRiskScore})",
                sessionId, assessment.RiskScore, requirement.MaxRiskScore);

            context.Fail(requirement);
        }

        await Task.CompletedTask;
    }

    private string GetSessionId(ToolApprovalContext context)
    {
        return context.Arguments.TryGetValue("sessionId", out var sessionId) && sessionId is string sid
            ? sid
            : "default";
    }
}
