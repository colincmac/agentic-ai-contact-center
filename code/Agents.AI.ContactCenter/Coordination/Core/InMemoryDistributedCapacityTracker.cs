using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// In-process <see cref="IDistributedCapacityTracker"/> backed by per-tier
/// <see cref="Interlocked"/>-managed counters. Suitable for dev / Aspire and
/// for the per-pod fallback in ADR-0004's cluster-local degraded-mode
/// admission contract.
/// </summary>
public sealed class InMemoryDistributedCapacityTracker : IDistributedCapacityTracker
{
    private readonly ConcurrentDictionary<AgentTier, Counter> _counters = new();

    public Task<CapacityAdmissionResult> TryAdmitAsync(AgentTier tier, long cap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var counter = _counters.GetOrAdd(tier, _ => new Counter());

        while (true)
        {
            var current = Volatile.Read(ref counter.Value);
            if (current >= cap)
            {
                return Task.FromResult(new CapacityAdmissionResult(false, current));
            }

            var next = current + 1;
            if (Interlocked.CompareExchange(ref counter.Value, next, current) == current)
            {
                return Task.FromResult(new CapacityAdmissionResult(true, next));
            }
        }
    }

    public Task ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_counters.TryGetValue(tier, out var counter))
        {
            return Task.CompletedTask;
        }

        while (true)
        {
            var current = Volatile.Read(ref counter.Value);
            if (current <= 0)
            {
                return Task.CompletedTask;
            }

            if (Interlocked.CompareExchange(ref counter.Value, current - 1, current) == current)
            {
                return Task.CompletedTask;
            }
        }
    }

    public Task<long> GetCountAsync(AgentTier tier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = _counters.TryGetValue(tier, out var counter)
            ? Volatile.Read(ref counter.Value)
            : 0L;
        return Task.FromResult(value);
    }

    private sealed class Counter
    {
        public long Value;
    }
}
