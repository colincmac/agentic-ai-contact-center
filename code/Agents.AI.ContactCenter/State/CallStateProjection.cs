using System.Text.Json;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Base class for state projections. Owns one slice's key, initializer, and JSON (de)serialization, and
/// turns a pure <see cref="Apply"/> reducer into a folded, persistable, renderable slice. The slice type
/// itself stays a plain immutable record — composition, not inheritance, for the data.
/// </summary>
/// <remarks>
/// Adding a new state type is purely additive: derive a projection, override <see cref="Apply"/> (and
/// optionally <see cref="Render"/>), and register it with one <c>TryAddEnumerable</c> line. No core
/// type changes. Keep <see cref="Apply"/> pure and free of I/O — the projector runs it on the call's
/// single event-pump thread, which is what removes the need for per-field locking. Reads
/// (<see cref="Read"/>) stay lock-free; mutation is funneled through the single-writer projector fold.
/// </remarks>
/// <typeparam name="TState">Immutable slice type (use a record so <c>with</c> produces new snapshots).</typeparam>
public abstract class CallStateProjection<TState> : ICallStateProjection
    where TState : class
{
    private readonly Func<TState> _initializer;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <param name="sliceId">Stable, unique slice id used as the persistence key. Treat renames as a data migration.</param>
    /// <param name="initializer">Produces the initial slice when none is present in the bag.</param>
    /// <param name="jsonOptions">Optional serializer options; defaults to <see cref="CallStateJson.Default"/>.</param>
    protected CallStateProjection(string sliceId, Func<TState> initializer, JsonSerializerOptions? jsonOptions = null)
    {
        SliceId = Throw.IfNullOrWhitespace(sliceId);
        _initializer = Throw.IfNull(initializer);
        _jsonOptions = jsonOptions ?? CallStateJson.Default;
    }

    /// <inheritdoc />
    public string SliceId { get; }

    /// <inheritdoc />
    public Type SnapshotType => typeof(TState);

    /// <summary>Lock-free read of the current slice, or the initialized default when absent.</summary>
    public TState Read(CallStateBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        return bag.TryGet<TState>(SliceId, out var state) && state is not null ? state : _initializer();
    }

    /// <summary>
    /// Pure reducer: fold one event into the slice. Return <paramref name="current"/> unchanged for a
    /// no-op (the projector skips the publish when the reference is unchanged).
    /// </summary>
    protected abstract TState Apply(TState current, StrategyEvent strategyEvent);

    /// <summary>Optional prompt rendering of the slice. Return <see langword="null"/> to contribute nothing.</summary>
    protected virtual string? Render(TState current) => null;

    object ICallStateProjection.ReadBoxed(CallStateBag bag) => Read(bag);

    void ICallStateProjection.Fold(CallStateBag bag, StrategyEvent strategyEvent)
    {
        var current = Read(bag);
        var next = Apply(current, strategyEvent);
        if (!ReferenceEquals(current, next))
        {
            bag.Set(SliceId, next);
        }
    }

    string? ICallStateProjection.Render(CallStateBag bag) => Render(Read(bag));

    string ICallStateProjection.Serialize(CallStateBag bag) => JsonSerializer.Serialize(Read(bag), _jsonOptions);

    void ICallStateProjection.Restore(CallStateBag bag, string json)
    {
        ArgumentNullException.ThrowIfNull(bag);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        var state = JsonSerializer.Deserialize<TState>(json, _jsonOptions);
        if (state is not null)
        {
            bag.Set(SliceId, state);
        }
    }
}
