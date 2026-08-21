namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Result of an intent classification operation performed by
/// <see cref="IvrIntentAgent"/>.
/// </summary>
public sealed class IntentResult
{
    /// <summary>
    /// The classified intent name, or null if no intent was matched.
    /// </summary>
    public string? IntentName { get; init; }

    /// <summary>
    /// Confidence score for the classification (0.0–1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Extracted entities from the utterance, keyed by entity name.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Entities { get; init; }

    /// <summary>
    /// Returns true when no intent was matched.
    /// </summary>
    public bool IsNone => IntentName is null;

    /// <summary>
    /// A singleton representing no matched intent.
    /// </summary>
    public static IntentResult None { get; } = new() { Confidence = 0.0 };
}
