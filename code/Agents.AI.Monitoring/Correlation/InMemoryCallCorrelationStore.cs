using System.Collections.Concurrent;

namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// In-process <see cref="ICallCorrelationStore"/>. The default registration; sufficient for
/// single-instance/dev and for live enrichment. For cross-pod durability register the Redis
/// store via <c>AddRedisCallCorrelationStore()</c>.
/// </summary>
internal sealed class InMemoryCallCorrelationStore : ICallCorrelationStore
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, CallCorrelationContext> _byE2E = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _index = new(StringComparer.Ordinal);

    public Task<CallCorrelationContext> GetOrCreateByServerCallIdAsync(
        CallCorrelationContext candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.AcsServerCallId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var existing = Resolve(candidate.AcsServerCallId);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            Store(candidate);
            return Task.FromResult(candidate);
        }
    }

    public Task UpsertAsync(CallCorrelationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Store(context);
        }
        return Task.CompletedTask;
    }

    public Task<CallCorrelationContext?> GetByE2ECallIdAsync(string e2eCallId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byE2E.GetValueOrDefault(e2eCallId));

    public Task<CallCorrelationContext?> GetByContextIdAsync(string contextId, CancellationToken cancellationToken = default)
        => Task.FromResult(Resolve(contextId));

    public Task<CallCorrelationContext?> GetByCallKeyAsync(string callKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Resolve(callKey));

    private CallCorrelationContext? Resolve(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (_byE2E.TryGetValue(key, out var direct))
        {
            return direct;
        }

        return _index.TryGetValue(key, out var e2e) ? _byE2E.GetValueOrDefault(e2e) : null;
    }

    private void Index(string? key, string e2eCallId)
    {
        if (!string.IsNullOrEmpty(key))
        {
            _index[key] = e2eCallId;
        }
    }

    private void Store(CallCorrelationContext context)
    {
        var merged = _byE2E.TryGetValue(context.E2ECallId, out var current)
            ? Merge(current, context)
            : context;
        _byE2E[merged.E2ECallId] = merged;
        Index(merged.ContextId, merged.E2ECallId);
        Index(merged.AcsServerCallId, merged.E2ECallId);
        Index(merged.AcsCallConnectionId, merged.E2ECallId);
    }

    private static CallCorrelationContext Merge(
        CallCorrelationContext current,
        CallCorrelationContext update)
        => current with
        {
            AcsServerCallId = update.AcsServerCallId ?? current.AcsServerCallId,
            AcsCallConnectionId = update.AcsCallConnectionId ?? current.AcsCallConnectionId,
            AcsCorrelationId = update.AcsCorrelationId ?? current.AcsCorrelationId,
            IngressTraceParent = update.IngressTraceParent ?? current.IngressTraceParent,
            D365ConversationId = update.D365ConversationId ?? current.D365ConversationId,
            CallerAniHash = update.CallerAniHash ?? current.CallerAniHash,
            CalledDid = update.CalledDid ?? current.CalledDid,
            Workstream = update.Workstream ?? current.Workstream,
            Queue = update.Queue ?? current.Queue,
            Intent = update.Intent ?? current.Intent,
            LanguageCode = update.LanguageCode ?? current.LanguageCode,
            MediaStreamingUri = update.MediaStreamingUri ?? current.MediaStreamingUri,
            CallBackUri = update.CallBackUri ?? current.CallBackUri
        };
}
