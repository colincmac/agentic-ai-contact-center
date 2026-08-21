namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Represents a complete prompt for a Realtime AI voice agent.
/// </summary>
public record RealtimePrompt
{
    /// <summary>
    /// Gets or sets the role and objective section defining who the agent is and success criteria.
    /// </summary>
    public RoleAndObjective? Role { get; init; }

    /// <summary>
    /// Gets or sets the personality and tone configuration.
    /// </summary>
    public PersonalityAndTone? Personality { get; init; }

    /// <summary>
    /// Gets or sets additional context information.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Gets or sets reference pronunciations for specific words.
    /// </summary>
    public IReadOnlyList<ReferencePronunciation>? ReferencePronunciations { get; init; }

    /// <summary>
    /// Gets or sets the tool configurations.
    /// </summary>
    public ToolConfiguration? Tools { get; init; }

    /// <summary>
    /// Gets or sets the instruction rules.
    /// </summary>
    public InstructionRules? Instructions { get; init; }

    /// <summary>
    /// Gets or sets the conversation flow states.
    /// </summary>
    public IReadOnlyList<ConversationState>? ConversationFlow { get; init; }

    /// <summary>
    /// Gets or sets sample phrases for variety.
    /// </summary>
    public SamplePhrases? Phrases { get; init; }

    /// <summary>
    /// Gets or sets safety and escalation rules.
    /// </summary>
    public SafetyAndEscalation? Safety { get; init; }

    /// <summary>
    /// Creates a new builder for constructing a <see cref="RealtimePrompt"/>.
    /// </summary>
    public static RealtimePromptBuilder CreateBuilder() => new();
}
