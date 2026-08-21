namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Directory of <c>call → owning pod</c> bindings used by ADR-0011's
/// callback dispatch and reaper paths. The owning pod is the one that holds
/// the bi-di media WebSocket (streaming calls) or — for verb calls — the
/// pod that accepted the call and is the natural local processor.
/// </summary>
/// <remarks>
/// <para>
/// All operations are scoped to a single <c>callConnectionId</c>. Hash-tagged
/// keys (<c>owner:{callConnectionId}</c>) keep cross-namespace coordination
/// for the same call colocated on one Redis shard (ADR-0004).
/// </para>
/// <para>
/// Lease semantics: the owning pod renews the lease via
/// <see cref="RenewAsync"/> at one-third of the configured
/// <see cref="Configuration.CallOwnershipOptions.LeaseDuration"/>. A missed
/// renewal lets Redis expire the key; the next <see cref="TryAcquireAsync"/>
/// call from any pod will succeed (the reaper path in ADR-0011).
/// </para>
/// </remarks>
public interface ICallOwnershipDirectory
{
    /// <summary>
    /// Attempts to claim ownership of <paramref name="callConnectionId"/> for
    /// the local pod with the supplied <paramref name="kind"/>.
    /// </summary>
    /// <returns>
    /// A result whose <see cref="CallOwnershipAcquireResult.Acquired"/> is
    /// <c>true</c> when the local pod is now the owner; <c>false</c> when
    /// another pod owns the call (the caller should forward per ADR-0011).
    /// </returns>
    Task<CallOwnershipAcquireResult> TryAcquireAsync(
        string callConnectionId,
        CallOwnershipKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current owner of <paramref name="callConnectionId"/> or
    /// <c>null</c> when no live owner exists.
    /// </summary>
    Task<CallOwnership?> GetOwnerAsync(
        string callConnectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease on <paramref name="callConnectionId"/> for the local
    /// pod. Returns <c>false</c> when the local pod is no longer the owner
    /// (another pod claimed the call after a missed renewal). Callers should
    /// treat <c>false</c> as a signal to release any local resources for the
    /// call.
    /// </summary>
    Task<bool> RenewAsync(
        string callConnectionId,
        CallOwnershipKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases ownership of <paramref name="callConnectionId"/>, but only if
    /// the local pod is still recorded as the owner. A reaped call (already
    /// owned by a different instance) is left alone.
    /// </summary>
    /// <returns><c>true</c> when the lease was removed by this call.</returns>
    Task<bool> ReleaseAsync(
        string callConnectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sweeps the directory for orphaned <c>owner:*</c> entries per
    /// ADR-0011's reaper path: an entry is reaped when its lease has
    /// expired <strong>and</strong> the owning pod's
    /// <c>pod:lease:{clusterId}:{podId}</c> is no longer alive (looked up
    /// via <paramref name="podLeases"/>). Reap claims are atomic
    /// compare-and-DEL so two reapers racing for the same orphan do not
    /// double-process.
    /// </summary>
    /// <returns>The number of orphaned entries removed by this sweep.</returns>
    Task<int> ReapOrphansAsync(
        IPodLeaseStore podLeases,
        CancellationToken cancellationToken = default);
}
