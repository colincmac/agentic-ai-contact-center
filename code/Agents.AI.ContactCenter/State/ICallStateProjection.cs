using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// A pluggable projection of the call's <see cref="StrategyEvent"/> stream into one immutable state
/// slice. Implementations fold events into the slice, render it into prompt text, and (de)serialize
/// it for the snapshot store. Register many via DI (<c>TryAddEnumerable</c>); the
/// <see cref="CallStateProjector"/> folds them all on the single event-pump thread.
/// </summary>
/// <remarks>
/// Authors normally derive from <see cref="CallStateProjection{TState}"/> rather than implementing this
/// directly, which keeps the slice type a plain immutable record and hides the boxing bridge.
/// Projections MUST be independent: a projection folds only the event stream and never reads another
/// projection's slice, otherwise fold ordering becomes load-bearing.
/// </remarks>
public interface ICallStateProjection
{
    /// <summary>Stable, unique slice id. Used as the persistence key — treat renames as a data migration.</summary>
    string SliceId { get; }

    /// <summary>CLR type of the slice snapshot, used for typed reader lookup on the projector.</summary>
    Type SnapshotType { get; }

    /// <summary>Lock-free read of the current slice snapshot as a boxed object, for the projector's typed reader.</summary>
    object ReadBoxed(CallStateBag bag);

    /// <summary>Fold one event into the slice. MUST be pure and called only by the single-writer projector.</summary>
    void Fold(CallStateBag bag, StrategyEvent strategyEvent);

    /// <summary>Render the current slice into prompt text, or <see langword="null"/> to contribute nothing.</summary>
    string? Render(CallStateBag bag);

    /// <summary>Serialize the current slice to JSON for the snapshot store.</summary>
    string Serialize(CallStateBag bag);

    /// <summary>Restore the slice from persisted JSON (called during hydrate, single-writer).</summary>
    void Restore(CallStateBag bag, string json);
}
