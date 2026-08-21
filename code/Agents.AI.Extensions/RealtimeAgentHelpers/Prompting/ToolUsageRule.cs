namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Defines usage rules for a specific tool.
/// </summary>
public record ToolUsageRule
{
    /// <summary>
    /// Gets or sets the tool name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets when to use this tool.
    /// </summary>
    public required string UseWhen { get; init; }

    /// <summary>
    /// Gets or sets when NOT to use this tool.
    /// </summary>
    public string? DoNotUseWhen { get; init; }

    /// <summary>
    /// Gets or sets the tool behavior type.
    /// </summary>
    public ToolBehavior Behavior { get; init; } = ToolBehavior.Proactive;

    /// <summary>
    /// Gets or sets sample preamble phrases for this tool.
    /// </summary>
    public IReadOnlyList<string>? PreamblePhrases { get; init; }

    /// <summary>
    /// Gets or sets a confirmation phrase (for ConfirmationFirst behavior).
    /// </summary>
    public string? ConfirmationPhrase { get; init; }
}
