namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Represents a conversation flow state.
/// </summary>
public record ConversationState
{
    /// <summary>
    /// Gets or sets the unique identifier for this state (e.g., "1_greeting").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets the description of this state's purpose.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets or sets the goal for this state.
    /// </summary>
    public string? Goal { get; init; }

    /// <summary>
    /// Gets or sets the instructions for this state.
    /// </summary>
    public required IReadOnlyList<string> Instructions { get; init; }

    /// <summary>
    /// Gets or sets example phrases for this state.
    /// </summary>
    public IReadOnlyList<string>? Examples { get; init; }

    /// <summary>
    /// Gets or sets the exit condition for this state.
    /// </summary>
    public string? ExitWhen { get; init; }

    /// <summary>
    /// Gets or sets the transitions from this state.
    /// </summary>
    public IReadOnlyList<StateTransition>? Transitions { get; init; }
}
