using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// In-process <see cref="IPodLeaseStore"/>. Suitable for single-pod dev /
/// Aspire scenarios where ADR-0011's cross-pod reaper is degenerate (only
/// the local pod ever exists). The shared dictionary is also useful for
/// tests that simulate multiple identities against a single process.
/// </summary>
public sealed class InMemoryPodLeaseStore : IPodLeaseStore
{
    private readonly ConcurrentDictionary<(string ClusterId, string PodId), Lease> _leases = new();
    private readonly IClusterIdentity _identity;
    private readonly TimeProvider _timeProvider;

    public InMemoryPodLeaseStore(IClusterIdentity identity, TimeProvider timeProvider)
    {
        _identity = identity;
        _timeProvider = timeProvider;
    }

    public Task RenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (_identity.ClusterId, _identity.PodId);
        var lease = new Lease(_identity.InstanceId, _timeProvider.GetUtcNow() + leaseDuration);
        _leases[key] = lease;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (_identity.ClusterId, _identity.PodId);
        while (_leases.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (_leases.TryRemove(new KeyValuePair<(string, string), Lease>(key, existing)))
            {
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsAliveAsync(string clusterId, string podId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_leases.TryGetValue((clusterId, podId), out var lease))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(lease.LeaseUntil > _timeProvider.GetUtcNow());
    }

    private readonly record struct Lease(string InstanceId, DateTimeOffset LeaseUntil);
}
