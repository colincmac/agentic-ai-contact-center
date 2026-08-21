namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Defines the agent's role and objective.
/// </summary>
public record RoleAndObjective
{
    /// <summary>
    /// Gets or sets who the agent is.
    /// </summary>
    public required string Identity { get; init; }

    /// <summary>
    /// Gets or sets what success looks like for this agent.
    /// </summary>
    public required string Objective { get; init; }

    /// <summary>
    /// Gets or sets optional accent or character traits.
    /// </summary>
    public string? CharacterTraits { get; init; }
}
