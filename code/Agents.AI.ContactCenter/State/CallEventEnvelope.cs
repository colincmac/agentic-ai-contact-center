using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Durable, ordered wrapper around a <see cref="StrategyEvent"/> for the optional append-only event
/// log. Carries the per-call monotonic <see cref="Sequence"/> that <see cref="StrategyEvent"/> itself
/// does not, enabling gap-free replay and snapshot watermarking.
/// </summary>
public sealed record CallEventEnvelope
{
    /// <summary>The call this event belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>1-based, gap-free, per-call sequence number.</summary>
    public required long Sequence { get; init; }

    /// <summary>The folded strategy event.</summary>
    public required StrategyEvent Event { get; init; }

    /// <summary>UTC time the event was appended to the log.</summary>
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}
