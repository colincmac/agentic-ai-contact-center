namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Represents a transition between conversation states.
/// </summary>
public record StateTransition
{
    /// <summary>
    /// Gets or sets the target state ID.
    /// </summary>
    public required string NextStep { get; init; }

    /// <summary>
    /// Gets or sets the condition for this transition.
    /// </summary>
    public required string Condition { get; init; }
}
