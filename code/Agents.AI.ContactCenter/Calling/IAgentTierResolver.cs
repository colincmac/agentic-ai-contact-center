using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Resolves the best available <see cref="AgentTier"/> for a new session and
/// atomically admits it to the per-tier capacity counter (ADR-0004).
/// </summary>
/// <remarks>
/// <para>
/// Resolution and admission are fused into a single operation: probing
/// capacity and then incrementing a counter cannot be safely split in a
/// horizontally scaled deployment, so <see cref="ResolveAsync"/> and
/// <see cref="ResolveFallbackAsync"/> both return only after the per-tier
/// counter has been incremented for the caller. Callers therefore call
/// <see cref="ReleaseAsync"/> exactly once per successful resolve, typically
/// when the call session is disposed.
/// </para>
/// <para>
/// Implementations must consult the active
/// <see cref="Coordination.ITierCeilingProvider"/> before admitting and must
/// skip any <see cref="AgentTier"/> ranked above the current ceiling per
/// ADR-0008.
/// </para>
/// </remarks>
public interface IAgentTierResolver
{
    /// <summary>
    /// Walks the configured <see cref="AgentTierOptions.FallbackOrder"/> (or
    /// starts from <paramref name="preferredTier"/> when supplied) and
    /// atomically admits a new session to the first enabled tier that is at
    /// or below the active ceiling and still under its effective capacity.
    /// </summary>
    /// <param name="preferredTier">
    /// Optional caller-requested tier. When set, the resolver tries this
    /// tier first; if it cannot be admitted, the resolver falls through to
    /// the remainder of the configured order.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The <see cref="AgentTier"/> the caller was admitted to.</returns>
    /// <exception cref="CapacityExhaustedException">
    /// Thrown when no tier in the configured order can admit the caller.
    /// </exception>
    ValueTask<AgentTier> ResolveAsync(AgentTier? preferredTier = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Walks the configured order starting at the tier immediately below
    /// <paramref name="currentTier"/> and atomically admits to the first
    /// enabled tier that fits the active ceiling and still has capacity.
    /// Used for mid-call degradation when the current transport fails.
    /// </summary>
    /// <param name="currentTier">The tier that failed or exceeded capacity.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The admitted lower <see cref="AgentTier"/>, or <c>null</c> when no
    /// lower tier has capacity. Callers that receive <c>null</c> must end
    /// the call.
    /// </returns>
    ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier currentTier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously admitted slot on the per-tier counter. Idempotent
    /// — the underlying counter is clamped at zero so double-release (call end
    /// plus reaper sweep) cannot drive it negative.
    /// </summary>
    ValueTask ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default);
}
