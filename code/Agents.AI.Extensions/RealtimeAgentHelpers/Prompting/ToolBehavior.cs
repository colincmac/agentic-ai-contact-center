namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Defines tool call behavior types.
/// </summary>
public enum ToolBehavior
{
    /// <summary>
    /// Call immediately without confirmation or preamble.
    /// </summary>
    Proactive,

    /// <summary>
    /// Ask for user confirmation before calling.
    /// </summary>
    ConfirmationFirst,

    /// <summary>
    /// Output a preamble phrase while calling.
    /// </summary>
    Preambles
}
