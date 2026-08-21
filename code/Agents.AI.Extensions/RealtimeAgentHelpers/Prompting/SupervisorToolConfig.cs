namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures the supervisor tool for responder-thinker architecture.
/// </summary>
public record SupervisorToolConfig
{
    public string ToolName { get; set; } = "getNextResponseFromSupervisor";
    /// <summary>
    /// Gets or sets when to call the supervisor.
    /// </summary>
    public required IReadOnlyList<string> CallWhen { get; init; }

    /// <summary>
    /// Gets or sets when NOT to call the supervisor.
    /// </summary>
    public required IReadOnlyList<string> DoNotCallWhen { get; init; }

    /// <summary>
    /// Gets or sets approved filler phrases while waiting.
    /// </summary>
    public IReadOnlyList<string>? ApprovedFillers { get; init; }

    /// <summary>
    /// Gets or sets instructions for rephrasing supervisor responses.
    /// </summary>
    public string? RephraseInstructions { get; init; }
}
