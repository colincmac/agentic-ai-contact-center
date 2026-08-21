namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Defines instruction rules for the agent.
/// </summary>
public record InstructionRules
{
    /// <summary>
    /// Gets or sets general rules the agent must follow.
    /// </summary>
    public IReadOnlyList<string>? GeneralRules { get; init; }

    /// <summary>
    /// Gets or sets instructions for handling unclear or inaudible audio.
    /// </summary>
    public UnclearAudioHandling? UnclearAudio { get; init; }

    /// <summary>
    /// Gets or sets rules for alphanumeric pronunciation.
    /// </summary>
    public string? AlphanumericPronunciation { get; init; }

    /// <summary>
    /// Gets or sets background sound suppression rules.
    /// </summary>
    public IReadOnlyList<string>? SuppressSounds { get; init; }
}
