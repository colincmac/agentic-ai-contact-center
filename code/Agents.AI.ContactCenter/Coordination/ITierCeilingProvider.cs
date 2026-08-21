using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Per-cluster active <see cref="AgentTier"/> ceiling for new-call admission
/// (ADR-0008). New calls cannot be admitted at a tier numerically lower than
/// <see cref="Current"/> (i.e., higher quality / less degraded), so the
/// <c>IncomingCall</c> handler must clamp its requested tier to at least
/// <c>Current</c> before stamping it onto the call's Redis state per
/// ADR-0004.
/// </summary>
/// <remarks>
/// <para>
/// Distributed implementations source the value from the
/// <c>ceiling:cluster:{clusterId}</c> key in Redis and stay in sync via the
/// matching Redis Pub/Sub channel, so the answer-path lookup of
/// <see cref="Current"/> is always in-process — no network round-trip per
/// incoming call. ADR-0008 mandates this caching shape.
/// </para>
/// <para>
/// The ceiling is per-cluster by design (ADR-0010): a regional realtime AI
/// outage in one cluster must not drag healthy clusters down. Operators and
/// the auto-degradation controller call <see cref="SetAsync"/> to lower or
/// raise the ceiling for the local cluster only.
/// </para>
/// </remarks>
public interface ITierCeilingProvider
{
    /// <summary>
    /// Cached active ceiling for the local cluster. Initially the configured
    /// <see cref="TierCeilingOptions.DefaultCeiling"/>; updated
    /// asynchronously when a Pub/Sub invalidation is received. Read on the
    /// answer path; never blocks.
    /// </summary>
    AgentTier Current { get; }

    /// <summary>
    /// Reads the latest value from the source of truth and updates
    /// <see cref="Current"/>. Use sparingly — admission code should rely on
    /// the cached value.
    /// </summary>
    Task<AgentTier> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the source of truth for the local cluster and broadcasts the
    /// change to all pods in the cluster. Called by the operator API or the
    /// auto-degradation controller, never on the answer path.
    /// </summary>
    Task SetAsync(AgentTier ceiling, CancellationToken cancellationToken = default);
}
