using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Optional append-only log of the raw <see cref="StrategyEvent"/> stream for one call, enabling audit
/// and replay-based projection rebuilds. Disabled by default (snapshot-only persistence); opt in via
/// <c>CallStateOptions.EnableEventLog</c>. Implementations: in-memory, Redis Streams, Cosmos.
/// </summary>
public interface ICallEventLog
{
    /// <summary>Append <paramref name="strategyEvent"/> to <paramref name="callId"/>'s log; returns the assigned sequence.</summary>
    ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default);

    /// <summary>Replay events after <paramref name="afterSequence"/> (0 = from the beginning), in order.</summary>
    IAsyncEnumerable<CallEventEnvelope> ReadAsync(string callId, long afterSequence = 0, CancellationToken cancellationToken = default);
}
