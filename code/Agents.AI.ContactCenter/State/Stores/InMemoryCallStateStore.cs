using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Exceptions;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// In-process <see cref="ICallStateStore"/>. Suitable for dev, Aspire, single-pod deployments, and the
/// degraded-mode fallback. Enforces the same optimistic-concurrency contract as the distributed stores
/// so behavior is identical across backends.
/// </summary>
public sealed class InMemoryCallStateStore : ICallStateStore
{
    private readonly ConcurrentDictionary<string, CallStateSnapshot> _calls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ValueTask<CallStateSnapshot> SaveAsync(CallStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var currentVersion = _calls.TryGetValue(snapshot.CallId, out var existing) ? existing.Version : 0;
            if (snapshot.Version != currentVersion)
            {
                throw new CallStateConcurrencyException(snapshot.CallId, snapshot.Version, currentVersion);
            }

            // EventSequence and Slices carry over from the incoming snapshot via the record copy below;
            // only the store-owned Version and UpdatedAt are reassigned.
            var persisted = snapshot with
            {
                Version = currentVersion + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _calls[snapshot.CallId] = persisted;
            return ValueTask.FromResult(persisted);
        }
    }

    public ValueTask<CallStateSnapshot?> LoadAsync(string callId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_calls.TryGetValue(callId, out var snapshot) ? snapshot : null);
    }

    public ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.TryRemove(callId, out _);
        return ValueTask.CompletedTask;
    }
}
