namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures safety and escalation rules.
/// </summary>
public record SafetyAndEscalation
{
    /// <summary>
    /// Gets or sets conditions that trigger escalation.
    /// </summary>
    public required IReadOnlyList<string> EscalateWhen { get; init; }

    /// <summary>
    /// Gets or sets what to say when escalating.
    /// </summary>
    public IReadOnlyList<string>? EscalationPhrases { get; init; }

    /// <summary>
    /// Gets or sets the maximum failed tool attempts before escalation.
    /// </summary>
    public int? MaxFailedToolAttempts { get; init; }

    /// <summary>
    /// Gets or sets the maximum consecutive no-match events before escalation.
    /// </summary>
    public int? MaxNoMatchEvents { get; init; }

    /// <summary>
    /// Gets or sets examples of scenarios requiring escalation.
    /// </summary>
    public IReadOnlyList<string>? EscalationExamples { get; init; }
}
