namespace Agents.AI.ContactCenter.Media.Signaling;

/// <summary>
/// Lightweight envelope for transport-level control signals such as DTMF tones,
/// hold/transfer requests, or VAD-originated events.
/// </summary>
public sealed class SessionSignal
{
    public required SessionSignalKind Kind { get; init; }

    /// <summary>Optional payload; interpretation depends on <see cref="Kind"/>.</summary>
    public string? Value { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
