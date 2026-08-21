using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Result of an atomic admission attempt against a per-tier capacity counter.
/// </summary>
/// <param name="Admitted">
/// <c>true</c> when the counter was incremented and the caller has been admitted;
/// <c>false</c> when the counter was already at or above the requested cap and
/// the caller must fall back (to a lower tier or to overflow).
/// </param>
/// <param name="Count">
/// The counter value <em>after</em> the operation. When
/// <see cref="Admitted"/> is <c>true</c> this is the new (post-increment)
/// value; when <c>false</c> this is the unchanged current value. Use for
/// telemetry / dashboards — admission code should not re-check it.
/// </param>
public readonly record struct CapacityAdmissionResult(bool Admitted, long Count);

/// <summary>
/// Per-tier admission counter for new <see cref="AgentTier"/> sessions
/// (ADR-0004 namespace <c>cap:tier:{tier}</c>, ADR-0008 soft/hard caps).
/// </summary>
/// <remarks>
/// <para>
/// The contract is <em>compare-and-increment in one round trip</em>:
/// <see cref="TryAdmitAsync"/> reads the current count, refuses if it is at
/// or above the supplied cap, otherwise atomically increments and returns
/// the new value. This is the single admission primitive the
/// <c>IAgentTierResolver</c> calls per ADR-0004 ("the single source of
/// admission truth").
/// </para>
/// <para>
/// Per ADR-0004 the counter is sharded by tier (each tier's key lands on a
/// different Redis shard via the literal-brace hash tag) and is implemented
/// in v1 as a single global <c>INCR</c>/<c>DECR</c> with a future evolution
/// to per-cluster local counters rolled up to a global view; the abstraction
/// hides the split.
/// </para>
/// </remarks>
public interface IDistributedCapacityTracker
{
    /// <summary>
    /// Atomically admits a new session at the given tier if the current
    /// count is strictly less than <paramref name="cap"/>.
    /// </summary>
    /// <param name="tier">The tier the session would run at.</param>
    /// <param name="cap">
    /// The maximum number of concurrent sessions allowed for this tier in
    /// this admission scope. Computed by the caller from
    /// <see cref="AgentTierOptions"/>, the active
    /// <see cref="ITierCeilingProvider"/> ceiling, and (during degraded
    /// mode) the cluster's configured share per ADR-0010.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<CapacityAdmissionResult> TryAdmitAsync(AgentTier tier, long cap, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements the counter for the given tier. Called on session end and
    /// by the ADR-0011 reaper when an orphaned <c>owner:*</c> lease is
    /// swept. Clamped at zero so a double-release cannot drive the counter
    /// negative.
    /// </summary>
    Task ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current count for the given tier. For telemetry and the
    /// pod heartbeat's periodic reconciliation; admission code must not
    /// read-then-increment because that race is what
    /// <see cref="TryAdmitAsync"/> exists to close.
    /// </summary>
    Task<long> GetCountAsync(AgentTier tier, CancellationToken cancellationToken = default);
}
