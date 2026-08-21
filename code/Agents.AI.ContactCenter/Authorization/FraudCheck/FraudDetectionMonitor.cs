using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

/// <summary>
/// Monitors conversations in real-time for fraud detection and suspicious behavior.
/// Can be run as a background agent or integrated into the conversation pipeline.
/// </summary>
public sealed class FraudDetectionMonitor : IFraudDetectionMonitor
{
    private readonly ILogger<FraudDetectionMonitor> _logger;
    private readonly ConcurrentDictionary<string, FraudAssessment> _sessionAssessments = new();
    private readonly FraudDetectionOptions _options;

    public FraudDetectionMonitor(
        FraudDetectionOptions? options = null,
        ILogger<FraudDetectionMonitor>? logger = null)
    {
        _options = options ?? new FraudDetectionOptions();
        _logger = logger ?? NullLogger<FraudDetectionMonitor>.Instance;
    }

    /// <summary>
    /// Analyzes a conversation turn for fraud indicators
    /// </summary>
    public async Task<FraudAssessment> AnalyzeTurnAsync(
        string sessionId,
        RealtimeConversationTurn turn,
        CancellationToken cancellationToken = default)
    {
        var assessment = _sessionAssessments.GetOrAdd(sessionId, _ => new FraudAssessment
        {
            SessionId = sessionId,
            StartedAt = DateTimeOffset.UtcNow
        });

        assessment.TotalTurns++;
        assessment.LastAnalyzedAt = DateTimeOffset.UtcNow;

        // Analyze the turn for fraud indicators
        await AnalyzeFraudIndicatorsAsync(assessment, turn, cancellationToken);

        // Update risk score
        assessment.RiskScore = CalculateRiskScore(assessment);
        assessment.RiskLevel = DetermineRiskLevel(assessment.RiskScore);

        if (assessment.RiskLevel >= FraudRiskLevel.High)
        {
            _logger.LogWarning(
                "High fraud risk detected for session {SessionId}. Risk score: {RiskScore}",
                sessionId, assessment.RiskScore);
        }

        return assessment;
    }

    /// <summary>
    /// Gets the current fraud assessment for a session
    /// </summary>
    public FraudAssessment? GetAssessment(string sessionId)
    {
        return _sessionAssessments.TryGetValue(sessionId, out var assessment) ? assessment : null;
    }

    /// <summary>
    /// Clears the fraud assessment for a session
    /// </summary>
    public void ClearAssessment(string sessionId)
    {
        _sessionAssessments.TryRemove(sessionId, out _);
    }

    private async Task AnalyzeFraudIndicatorsAsync(
        FraudAssessment assessment,
        RealtimeConversationTurn turn,
        CancellationToken cancellationToken)
    {
        // Check for rapid-fire requests
        if (turn.Timestamp - assessment.LastAnalyzedAt < TimeSpan.FromSeconds(1))
        {
            assessment.RapidRequestCount++;
        }

            // Check for sensitive information requests
            if (ContainsSensitiveKeywords(turn.UserMessageText))
            {
                assessment.SensitiveInfoRequestCount++;
                assessment.FraudIndicators.Add(new FraudIndicator
                {
                    Type = FraudIndicatorType.SensitiveInfoRequest,
                    Timestamp = turn.Timestamp ?? DateTimeOffset.UtcNow,
                    Description = "Request for sensitive information detected",
                    Severity = IndicatorSeverity.Medium
                });
            }

            // Check for social engineering patterns
            if (ContainsSocialEngineeringPatterns(turn.UserMessageText))
            {
                assessment.SocialEngineeringAttempts++;
                assessment.FraudIndicators.Add(new FraudIndicator
                {
                    Type = FraudIndicatorType.SocialEngineering,
                    Timestamp = turn.Timestamp ?? DateTimeOffset.UtcNow,
                    Description = "Social engineering pattern detected",
                    Severity = IndicatorSeverity.High
                });
            }

            // Check for authentication bypass attempts
            if (ContainsAuthBypassPatterns(turn.UserMessageText))
            {
                assessment.AuthBypassAttempts++;
                assessment.FraudIndicators.Add(new FraudIndicator
                {
                    Type = FraudIndicatorType.AuthenticationBypass,
                    Timestamp = turn.Timestamp ?? DateTimeOffset.UtcNow,
                    Description = "Authentication bypass attempt detected",
                    Severity = IndicatorSeverity.Critical
                });
            }

            await Task.CompletedTask;
        }

    private double CalculateRiskScore(FraudAssessment assessment)
    {
        double score = 0.0;

        // Weight different indicators
        score += assessment.RapidRequestCount * 5.0;
        score += assessment.SensitiveInfoRequestCount * 10.0;
        score += assessment.SocialEngineeringAttempts * 25.0;
        score += assessment.AuthBypassAttempts * 50.0;
        score += assessment.UnusualBehaviorCount * 15.0;

        // Cap at 100
        return Math.Min(score, 100.0);
    }

    private FraudRiskLevel DetermineRiskLevel(double riskScore)
    {
        return riskScore switch
        {
            >= 75.0 => FraudRiskLevel.Critical,
            >= 50.0 => FraudRiskLevel.High,
            >= 25.0 => FraudRiskLevel.Medium,
            >= 10.0 => FraudRiskLevel.Low,
            _ => FraudRiskLevel.None
        };
    }

    private bool ContainsSensitiveKeywords(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var sensitiveKeywords = new[]
        {
            "password", "pin", "ssn", "social security", "credit card",
            "account number", "routing number", "secret", "token"
        };

        return sensitiveKeywords.Any(keyword =>
            message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private bool ContainsSocialEngineeringPatterns(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var patterns = new[]
        {
            "urgent", "immediate action", "verify your account",
            "suspended", "unusual activity", "security alert",
            "click here", "confirm your identity"
        };

        return patterns.Any(pattern =>
            message.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private bool ContainsAuthBypassPatterns(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var patterns = new[]
        {
            "skip verification", "bypass", "without authentication",
            "no password", "guest access", "temporary access"
        };

        return patterns.Any(pattern =>
            message.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask DisposeAsync()
    {
        _sessionAssessments.Clear();
        await Task.CompletedTask;
    }
}

public sealed class FraudAssessment
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastAnalyzedAt { get; set; }
    public int TotalTurns { get; set; }
    public double RiskScore { get; set; }
    public FraudRiskLevel RiskLevel { get; set; }
    
    public int RapidRequestCount { get; set; }
    public int SensitiveInfoRequestCount { get; set; }
    public int SocialEngineeringAttempts { get; set; }
    public int AuthBypassAttempts { get; set; }
    public int UnusualBehaviorCount { get; set; }
    
    public List<FraudIndicator> FraudIndicators { get; set; } = new();
}

public sealed class FraudIndicator
{
    public FraudIndicatorType Type { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Description { get; set; } = string.Empty;
    public IndicatorSeverity Severity { get; set; }
}

public enum FraudIndicatorType
{
    SensitiveInfoRequest,
    SocialEngineering,
    AuthenticationBypass,
    RapidRequests,
    UnusualPattern,
    SuspiciousLocation,
    AnomalousVoicePattern
}

public enum IndicatorSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum FraudRiskLevel
{
    None,
    Low,
    Medium,
    High,
    Critical
}



