using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// The per-call emit funnel for state folding. Wraps a strategy's downstream <see cref="StrategyEvent"/>
/// writer and folds every emitted event into the call's <see cref="CallStateProjector"/>
/// <em>synchronously</em> (under the projector's single fold lock) before forwarding it downstream to
/// the session pump and observers.
/// </summary>
/// <remarks>
/// Provides the workflow executor, predicates, and per-call tools read-after-write consistency against the projector:
/// code that emits an event and then reads <see cref="CallStateProjector.Get{TState}"/> on the same call
/// stack observes the folded result.
/// The projector is resolved lazily via <paramref name="projector"/>
/// so the funnel can be constructed before the call session is bound to the scope; it no-ops when no
/// projector is registered (the opt-in <c>AddCallState</c> path is absent).
/// </remarks>
public sealed class StateFoldingChannelWriter : ChannelWriter<StrategyEvent>
{
    private readonly ChannelWriter<StrategyEvent> _inner;
    private readonly Func<CallStateProjector?> _projector;

    public StateFoldingChannelWriter(ChannelWriter<StrategyEvent> inner, Func<CallStateProjector?> projector)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(projector);
        _inner = inner;
        _projector = projector;
    }

    public override bool TryWrite(StrategyEvent item)
    {
        _projector()?.Fold(item);
        return _inner.TryWrite(item);
    }

    public override ValueTask WriteAsync(StrategyEvent item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _projector()?.Fold(item);
        return _inner.WriteAsync(item, cancellationToken);
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => _inner.WaitToWriteAsync(cancellationToken);

    public override bool TryComplete(Exception? error = null) => _inner.TryComplete(error);
}
