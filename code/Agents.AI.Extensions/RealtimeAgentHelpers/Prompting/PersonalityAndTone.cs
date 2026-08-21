namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures the personality and tone of the voice agent.
/// </summary>
public record PersonalityAndTone
{
    /// <summary>
    /// Gets or sets the personality description (e.g., "Friendly, calm and approachable").
    /// </summary>
    public required string Personality { get; init; }

    /// <summary>
    /// Gets or sets the tone (e.g., "Warm, concise, confident, never fawning").
    /// </summary>
    public required string Tone { get; init; }

    /// <summary>
    /// Gets or sets the default response length (e.g., "2-3 sentences per turn").
    /// </summary>
    public string? Length { get; init; }

    /// <summary>
    /// Gets or sets the language constraint.
    /// </summary>
    public LanguageConstraint? Language { get; init; }

    /// <summary>
    /// Gets or sets pacing instructions (e.g., "Deliver fast but not rushed").
    /// </summary>
    public string? Pacing { get; init; }

    /// <summary>
    /// Gets or sets the level of enthusiasm.
    /// </summary>
    public string? Enthusiasm { get; init; }

    /// <summary>
    /// Gets or sets the level of formality.
    /// </summary>
    public string? Formality { get; init; }

    /// <summary>
    /// Gets or sets the level of emotion expression.
    /// </summary>
    public string? Emotion { get; init; }

    /// <summary>
    /// Gets or sets filler word usage (e.g., "none", "occasionally", "often").
    /// </summary>
    public string? FillerWords { get; init; }

    /// <summary>
    /// Gets or sets whether variety should be enforced to avoid repetition.
    /// </summary>
    public bool EnforceVariety { get; init; }
}
