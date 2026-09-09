namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Point-in-time snapshot of every state slice for one call. This is the unit the required
/// <see cref="ICallStateStore"/> persists. Slices are kept as opaque JSON keyed by slice id so the
/// store stays agnostic to which providers are registered — adding a provider needs no store change,
/// and an old snapshot that predates a provider simply leaves that slice at its initial value.
/// </summary>
public sealed record CallStateSnapshot
{
    /// <summary>The call this snapshot belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>
    /// Optimistic-concurrency token. A store increments it on each successful save; a save whose
    /// <see cref="Version"/> is stale throws <see cref="CallStateConcurrencyException"/>.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Replay watermark: the <see cref="CallEventEnvelope.Sequence"/> of the last event folded into this
    /// snapshot. Slices represent exactly that ordered prefix, never newer in-process folds.
    /// On hydrate, the projector replays the event log <c>afterSequence: EventSequence</c> to
    /// catch up the tail persisted since this snapshot. Stays <c>0</c> when the event log is disabled
    /// (the default) or no events have been folded yet.
    /// </summary>
    public long EventSequence { get; init; }

    /// <summary>Serialized slices keyed by <see cref="ICallStateProjection.SliceId"/>.</summary>
    public required IReadOnlyDictionary<string, string> Slices { get; init; }

    /// <summary>UTC time this snapshot was produced.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
