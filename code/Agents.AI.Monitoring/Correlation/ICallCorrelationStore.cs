namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// Persists the mapping between the canonical <see cref="CallCorrelationContext.E2ECallId"/>
/// and the per-boundary identifiers (ACS server/connection ids, context id, D365 conversation id).
/// Enables durable, post-call correlation joins and re-hydration of the ambient context on
/// each stateless webhook callback. Implementations index by every known key so a lookup by
/// any boundary identifier resolves the same context.
/// </summary>
public interface ICallCorrelationStore
{
    /// <summary>
    /// Atomically return the canonical context for an ACS server call id, creating it from
    /// <paramref name="candidate"/> only when no context exists.
    /// </summary>
    Task<CallCorrelationContext> GetOrCreateByServerCallIdAsync(
        CallCorrelationContext candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Insert or update the context, (re)indexing every known identifier.</summary>
    Task UpsertAsync(CallCorrelationContext context, CancellationToken cancellationToken = default);

    /// <summary>Resolve by the canonical end-to-end call id.</summary>
    Task<CallCorrelationContext?> GetByE2ECallIdAsync(string e2eCallId, CancellationToken cancellationToken = default);

    /// <summary>Resolve by the transfer context id.</summary>
    Task<CallCorrelationContext?> GetByContextIdAsync(string contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve by an ACS call key — matches against the server call id, call-connection id,
    /// end-to-end id, or context id. Use this from call-scoped code that only knows the
    /// session's <c>CallId</c> (which may be either ACS identifier).
    /// </summary>
    Task<CallCorrelationContext?> GetByCallKeyAsync(string callKey, CancellationToken cancellationToken = default);
}
