namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Per-pod heartbeat lease primitive used by ADR-0011's two-level lease
/// scheme. The local pod writes <c>pod:lease:{clusterId}:{podId}</c> on the
/// heartbeat tick; the reaper consults <see cref="IsAliveAsync"/> when
/// deciding whether to reap an orphaned <c>owner:*</c> entry.
/// </summary>
/// <remarks>
/// The lease value carries the local <see cref="IClusterIdentity.InstanceId"/>
/// so a pod restart (fresh GUID) is observable as an instanceId change even
/// when the same <see cref="IClusterIdentity.PodId"/> is reused by a
/// re-launched StatefulSet ordinal.
/// </remarks>
public interface IPodLeaseStore
{
    /// <summary>
    /// Writes / renews the local pod's lease with the supplied
    /// <paramref name="leaseDuration"/> TTL. Typically invoked by
    /// <see cref="IPodHeartbeat"/> on a 30 s tick with a 90 s lease per
    /// ADR-0011.
    /// </summary>
    Task RenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the local pod's lease, but only when the stored value still
    /// matches the local <see cref="IClusterIdentity.InstanceId"/> (so a
    /// re-launched process does not delete the new incarnation's lease).
    /// Called during graceful shutdown.
    /// </summary>
    Task ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when <c>pod:lease:{clusterId}:{podId}</c> exists
    /// (i.e., the referenced pod has heartbeat within the lease window). Used
    /// by the ADR-0011 reaper to distinguish a dead pod from a live pod that
    /// is merely slow to renew an individual call lease.
    /// </summary>
    Task<bool> IsAliveAsync(string clusterId, string podId, CancellationToken cancellationToken = default);
}
