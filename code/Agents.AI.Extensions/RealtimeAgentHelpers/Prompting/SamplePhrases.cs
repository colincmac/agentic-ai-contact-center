namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures sample phrases for variety.
/// </summary>
public record SamplePhrases
{
    /// <summary>
    /// Gets or sets acknowledgement phrases.
    /// </summary>
    public IReadOnlyList<string>? Acknowledgements { get; init; }

    /// <summary>
    /// Gets or sets clarifier phrases.
    /// </summary>
    public IReadOnlyList<string>? Clarifiers { get; init; }

    /// <summary>
    /// Gets or sets bridge/transition phrases.
    /// </summary>
    public IReadOnlyList<string>? Bridges { get; init; }

    /// <summary>
    /// Gets or sets empathy phrases.
    /// </summary>
    public IReadOnlyList<string>? Empathy { get; init; }

    /// <summary>
    /// Gets or sets closing phrases.
    /// </summary>
    public IReadOnlyList<string>? Closers { get; init; }
}
