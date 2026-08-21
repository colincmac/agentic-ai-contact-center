namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Defines a pronunciation guide for a specific term.
/// </summary>
public record ReferencePronunciation
{
    /// <summary>
    /// Gets or sets the word to pronounce.
    /// </summary>
    public required string Word { get; init; }

    /// <summary>
    /// Gets or sets the phonetic pronunciation guide.
    /// </summary>
    public required string Pronunciation { get; init; }
}
