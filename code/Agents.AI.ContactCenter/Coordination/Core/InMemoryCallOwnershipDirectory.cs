using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// In-process <see cref="ICallOwnershipDirectory"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Suitable for single-pod
/// dev / Aspire and for the per-pod fallback in ADR-0004's degraded-mode
/// admission contract; not suitable for the cross-pod callback dispatch
/// described in ADR-0011.
/// </summary>
public sealed class InMemoryCallOwnershipDirectory : ICallOwnershipDirectory
{
    private readonly ConcurrentDictionary<string, CallOwnership> _owners = new(StringComparer.Ordinal);
    private readonly IClusterIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;

    public InMemoryCallOwnershipDirectory(
        IClusterIdentity identity,
        IOptions<HyperscaleOptions> options,
        TimeProvider timeProvider)
    {
        _identity = identity;
        _timeProvider = timeProvider;
        _leaseDuration = options.Value.CallOwnership.LeaseDuration;
    }

    public Task<CallOwnershipAcquireResult> TryAcquireAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var newOwner = BuildLocalOwner(kind, now);

            if (_owners.TryAdd(callConnectionId, newOwner))
            {
                return Task.FromResult(new CallOwnershipAcquireResult(true, newOwner));
            }

            if (!_owners.TryGetValue(callConnectionId, out var existing))
            {
                continue;
            }

            if (existing.LeaseUntil > now)
            {
                return Task.FromResult(new CallOwnershipAcquireResult(false, existing));
            }

            // Lease expired — race to take over. Loser retries with a fresh now/lease window.
            if (_owners.TryUpdate(callConnectionId, newOwner, existing))
            {
                return Task.FromResult(new CallOwnershipAcquireResult(true, newOwner));
            }
        }
    }

    public Task<CallOwnership?> GetOwnerAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_owners.TryGetValue(callConnectionId, out var existing))
        {
            return Task.FromResult<CallOwnership?>(null);
        }

        if (existing.LeaseUntil <= _timeProvider.GetUtcNow())
        {
            return Task.FromResult<CallOwnership?>(null);
        }

        return Task.FromResult<CallOwnership?>(existing);
    }

    public Task<bool> RenewAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (!_owners.TryGetValue(callConnectionId, out var existing))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(existing.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            var renewed = BuildLocalOwner(kind, _timeProvider.GetUtcNow());
            if (_owners.TryUpdate(callConnectionId, renewed, existing))
            {
                return Task.FromResult(true);
            }
        }
    }

    public Task<bool> ReleaseAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (!_owners.TryGetValue(callConnectionId, out var existing))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(existing.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            if (_owners.TryRemove(new KeyValuePair<string, CallOwnership>(callConnectionId, existing)))
            {
                return Task.FromResult(true);
            }
        }
    }

    public async Task<int> ReapOrphansAsync(IPodLeaseStore podLeases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podLeases);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var reaped = 0;

        foreach (var entry in _owners)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Value.LeaseUntil > now)
            {
                continue;
            }

            var alive = await podLeases.IsAliveAsync(entry.Value.ClusterId, entry.Value.PodId, cancellationToken).ConfigureAwait(false);
            if (alive)
            {
                continue;
            }

            if (_owners.TryRemove(new KeyValuePair<string, CallOwnership>(entry.Key, entry.Value)))
            {
                reaped++;
            }
        }

        return reaped;
    }

    private CallOwnership BuildLocalOwner(CallOwnershipKind kind, DateTimeOffset now) =>
        new(_identity.ClusterId, _identity.PodId, _identity.InstanceId, kind, now + _leaseDuration);
}
