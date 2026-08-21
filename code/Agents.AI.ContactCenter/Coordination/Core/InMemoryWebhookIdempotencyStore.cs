using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// In-process <see cref="IWebhookIdempotencyStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> with lazy expiration.
/// Suitable for single-pod dev / Aspire and for the per-pod fallback
/// described in ADR-0004's degraded-mode admission contract.
/// </summary>
public sealed class InMemoryWebhookIdempotencyStore : IWebhookIdempotencyStore
{
    // Sweep on every Nth successful insert to bound memory without a timer.
    private const int SweepInterval = 1024;

    private readonly ConcurrentDictionary<(string CallConnectionId, int SequenceNumber), DateTimeOffset> _entries = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private long _writes;

    public InMemoryWebhookIdempotencyStore(IOptions<HyperscaleOptions> options, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _ttl = options.Value.WebhookIdempotency.TokenLifetime;
    }

    public Task<bool> TryRegisterAsync(string callConnectionId, int sequenceNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var expires = now + _ttl;
        var key = (callConnectionId, sequenceNumber);

        while (true)
        {
            if (_entries.TryAdd(key, expires))
            {
                MaybeSweep(now);
                return Task.FromResult(true);
            }

            if (!_entries.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (existing > now)
            {
                return Task.FromResult(false);
            }

            // Token expired — race to replace it; if we lose the race, the loop retries.
            if (_entries.TryUpdate(key, expires, existing))
            {
                MaybeSweep(now);
                return Task.FromResult(true);
            }
        }
    }

    private void MaybeSweep(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _writes) % SweepInterval != 0)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (pair.Value <= now)
            {
                _entries.TryRemove(pair);
            }
        }
    }
}
