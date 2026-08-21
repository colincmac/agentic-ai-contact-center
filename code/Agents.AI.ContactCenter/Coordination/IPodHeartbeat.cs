namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Cooperative tracker of locally-owned calls for the ADR-0011 pod
/// heartbeat. Call lifecycle code (the <c>CallSessionFactory</c> on
/// acquire, <c>CallSession.EndAsync</c> on release) reports owned calls
/// here so the heartbeat tick can renew each call's <c>owner:*</c> lease
/// in lockstep with the pod's own <c>pod:lease:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// The backing implementation is a hosted background service that:
/// </para>
/// <list type="number">
/// <item>Renews the local <c>pod:lease:{clusterId}:{podId}</c> at the
/// configured heartbeat interval (default 30 s) with a 90 s TTL.</item>
/// <item>Renews every tracked owned-call lease in the same tick. A renewal
/// that returns <c>false</c> (the call was reaped by another pod) is
/// untracked silently — the per-call <c>CallSession</c> learns about the
/// loss on its next operation against the directory.</item>
/// <item>Periodically invokes
/// <see cref="ICallOwnershipDirectory.ReapOrphansAsync"/> to sweep
/// <c>owner:*</c> entries whose owning pod is no longer alive.</item>
/// </list>
/// <para>
/// Tracking is idempotent and best-effort. Untracking a call that is not
/// currently tracked is a no-op.
/// </para>
/// </remarks>
public interface IPodHeartbeat
{
    /// <summary>
    /// Begin tracking <paramref name="callConnectionId"/> as owned by the
    /// local pod. The next heartbeat tick will renew its lease.
    /// </summary>
    void TrackOwnedCall(string callConnectionId, CallOwnershipKind kind);

    /// <summary>
    /// Stop tracking <paramref name="callConnectionId"/>. Called after the
    /// owning <see cref="ICallOwnershipDirectory.ReleaseAsync"/>.
    /// </summary>
    void UntrackOwnedCall(string callConnectionId);

    /// <summary>
    /// Snapshot of currently tracked calls. Intended for telemetry and
    /// tests; enumeration is not transactional.
    /// </summary>
    IReadOnlyDictionary<string, CallOwnershipKind> TrackedCalls { get; }
}
