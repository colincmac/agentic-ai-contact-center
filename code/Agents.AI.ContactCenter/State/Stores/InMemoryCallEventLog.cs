using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// In-process <see cref="ICallEventLog"/>. Append-only per-call list with a monotonic sequence,
/// suitable for dev and tests. Distributed deployments use the Redis or Cosmos log.
/// </summary>
public sealed class InMemoryCallEventLog : ICallEventLog
{
    private sealed class Log
    {
        public readonly List<CallEventEnvelope> Events = [];
        public long Sequence;
    }

    private readonly ConcurrentDictionary<string, Log> _calls = new(StringComparer.Ordinal);

    public ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(strategyEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var log = _calls.GetOrAdd(callId, static _ => new Log());
        lock (log)
        {
            var sequence = ++log.Sequence;
            log.Events.Add(new CallEventEnvelope
            {
                CallId = callId,
                Sequence = sequence,
                Event = strategyEvent,
            });
            return ValueTask.FromResult(sequence);
        }
    }

    public async IAsyncEnumerable<CallEventEnvelope> ReadAsync(
        string callId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallEventEnvelope[] snapshot;
        if (_calls.TryGetValue(callId, out var log))
        {
            lock (log)
            {
                snapshot = log.Events.Where(e => e.Sequence > afterSequence).ToArray();
            }
        }
        else
        {
            snapshot = [];
        }

        foreach (var envelope in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return envelope;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
