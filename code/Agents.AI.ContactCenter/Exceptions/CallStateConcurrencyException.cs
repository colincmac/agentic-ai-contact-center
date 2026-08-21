namespace Agents.AI.ContactCenter.Exceptions;

/// <summary>
/// Thrown by <see cref="ICallStateStore.SaveAsync"/> when the snapshot being saved is based on a stale
/// <see cref="CallStateSnapshot.Version"/>, indicating another writer advanced the call state.
/// </summary>
public sealed class CallStateConcurrencyException(string callId, long expectedVersion, long actualVersion)
    : Exception($"Concurrent update to call state '{callId}': expected version {expectedVersion} but the store had {actualVersion}.")
{
    /// <summary>The call whose state update conflicted.</summary>
    public string CallId { get; } = callId;

    /// <summary>The version the caller expected to overwrite.</summary>
    public long ExpectedVersion { get; } = expectedVersion;

    /// <summary>The version actually present in the store.</summary>
    public long ActualVersion { get; } = actualVersion;
}
