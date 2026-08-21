using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

/// <summary>
/// AI tools for fraud monitoring and assessment
/// </summary>
public sealed class FraudMonitoringTools
{
    private readonly FraudDetectionMonitor _fraudMonitor;
    private readonly ILogger<FraudMonitoringTools> _logger;

    public FraudMonitoringTools(
        FraudDetectionMonitor fraudMonitor,
        ILogger<FraudMonitoringTools>? logger = null)
    {
        _fraudMonitor = fraudMonitor;
        _logger = logger ?? NullLogger<FraudMonitoringTools>.Instance;
    }

    [Description("Gets the current fraud risk assessment for the session")]
    public async Task<object> GetFraudAssessmentAsync(
        [Description("The session ID")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        var assessment = _fraudMonitor.GetAssessment(sessionId);

        if (assessment is null)
        {
            return new
            {
                riskLevel = "none",
                riskScore = 0.0,
                message = "No fraud assessment available yet"
            };
        }

        return new
        {
            riskLevel = assessment.RiskLevel.ToString(),
            riskScore = assessment.RiskScore,
            totalTurns = assessment.TotalTurns,
            indicators = assessment.FraudIndicators.Select(i => new
            {
                type = i.Type.ToString(),
                severity = i.Severity.ToString(),
                description = i.Description,
                timestamp = i.Timestamp
            }),
            rapidRequestCount = assessment.RapidRequestCount,
            sensitiveInfoRequestCount = assessment.SensitiveInfoRequestCount,
            socialEngineeringAttempts = assessment.SocialEngineeringAttempts,
            authBypassAttempts = assessment.AuthBypassAttempts
        };
    }
}
