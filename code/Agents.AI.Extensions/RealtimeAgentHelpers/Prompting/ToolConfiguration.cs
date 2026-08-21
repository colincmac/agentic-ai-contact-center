namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures tool usage behavior.
/// </summary>
public record ToolConfiguration
{
    /// <summary>
    /// Gets or sets the global preamble instruction before tool calls.
    /// </summary>
    public string? GlobalPreamble { get; init; }

    /// <summary>
    /// Gets or sets whether confirmation is required before tool calls.
    /// </summary>
    public bool RequireConfirmation { get; init; }

    /// <summary>
    /// Gets or sets individual tool configurations.
    /// </summary>
    public IReadOnlyList<ToolUsageRule>? ToolRules { get; init; }

    /// <summary>
    /// Gets or sets supervisor tool configuration for responder-thinker architecture.
    /// </summary>
    public SupervisorToolConfig? SupervisorTool { get; init; }
}
