namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Idempotency token store for at-least-once webhook delivery (ADR-0004).
/// Mid-call ACS Call Automation callbacks may be duplicated by retries; the
/// store admits the first observation of <c>(callConnectionId, sequenceNumber)</c>
/// and rejects subsequent ones for the configured token lifetime.
/// </summary>
/// <remarks>
/// Per ADR-0011 the cross-pod webhook forwarder runs the dedup check
/// <em>before</em> forwarding, so a duplicate Event Grid delivery cannot
/// duplicate the forwarded call.
/// </remarks>
public interface IWebhookIdempotencyStore
{
    /// <summary>
    /// Atomically registers the dedup token for the given mid-call event.
    /// </summary>
    /// <param name="callConnectionId">ACS Call Automation call-connection identifier.</param>
    /// <param name="sequenceNumber">Monotonic per-call event sequence from the ACS callback payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> if this is the first observation of the tuple within the
    /// token lifetime — the caller MUST proceed with state-transition
    /// processing. <c>false</c> if the tuple has been (or is being) processed
    /// by another handler — the caller MUST short-circuit and return 200 OK.
    /// </returns>
    Task<bool> TryRegisterAsync(string callConnectionId, int sequenceNumber, CancellationToken cancellationToken = default);
}
