namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Persists the projected per-call state snapshot (all slices). This is the required persistence seam,
/// analogous to agent-framework's session <c>StateBag</c> but keyed by call id and versioned for
/// optimistic concurrency across pods. Implementations: in-memory (default), Redis, and Cosmos.
/// </summary>
/// <remarks>
/// Under ADR-0011 the call ownership directory guarantees a single owning pod per call, so in steady
/// state there is exactly one writer. The <see cref="CallStateSnapshot.Version"/> check is
/// defense-in-depth for the brief hand-off window during failover or tier swaps.
/// </remarks>
public interface ICallStateStore
{
    /// <summary>
    /// Save <paramref name="snapshot"/>, enforcing optimistic concurrency on
    /// <see cref="CallStateSnapshot.Version"/>. Returns the persisted snapshot with its incremented
    /// version. Throws <see cref="CallStateConcurrencyException"/> when the stored version has moved on.
    /// </summary>
    ValueTask<CallStateSnapshot> SaveAsync(CallStateSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Load the latest snapshot for <paramref name="callId"/>, or <see langword="null"/> when none exists.</summary>
    ValueTask<CallStateSnapshot?> LoadAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Delete all persisted state for <paramref name="callId"/>. Idempotent.</summary>
    ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default);
}

