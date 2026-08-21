namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures handling of unclear audio input.
/// </summary>
public record UnclearAudioHandling
{
    /// <summary>
    /// Gets or sets whether to ask for clarification on unclear audio.
    /// </summary>
    public bool AskForClarification { get; init; } = true;

    /// <summary>
    /// Gets or sets whether to repeat the last question instead.
    /// </summary>
    public bool RepeatLastQuestion { get; init; }

    /// <summary>
    /// Gets or sets sample clarification phrases.
    /// </summary>
    public IReadOnlyList<string>? ClarificationPhrases { get; init; }
}
