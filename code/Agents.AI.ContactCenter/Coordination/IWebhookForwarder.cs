namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Forwards a mid-call webhook payload to the pod that owns the call per
/// ADR-0011. Streaming-mode callbacks that land on a non-owning pod must
/// be replayed on the owning pod because the bi-di media WebSocket lives
/// there; verb-mode callbacks can be processed locally and never need this.
/// </summary>
public interface IWebhookForwarder
{
    /// <summary>
    /// Attempts to forward <paramref name="body"/> to the owning pod.
    /// </summary>
    /// <param name="owner">Ownership snapshot, typically the result of <see cref="ICallOwnershipDirectory.GetOwnerAsync"/>.</param>
    /// <param name="callbackPath">Original request path + query so the receiver can replay it through its normal callback handler (carried in the <c>X-Forwarded-Callback-Path</c> header).</param>
    /// <param name="body">Original webhook body, replayed verbatim.</param>
    /// <param name="contentType">Original <c>Content-Type</c> header.</param>
    /// <param name="headers">Optional additional headers to copy through (e.g. tracing).</param>
    /// <param name="cancellationToken">Caller cancellation; usually the inbound webhook's request token.</param>
    /// <returns>
    /// A <see cref="WebhookForwardResult"/> the caller uses to decide whether
    /// to short-circuit (success) or drop to the reaper path / local fallback.
    /// </returns>
    Task<WebhookForwardResult> TryForwardAsync(
        CallOwnership owner,
        string callbackPath,
        ReadOnlyMemory<byte> body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Disposition of a cross-pod forward attempt.
/// </summary>
public enum WebhookForwardOutcome
{
    /// <summary>
    /// The owning pod accepted the forwarded request (2xx). The caller may
    /// safely return 200 to the upstream webhook publisher.
    /// </summary>
    Forwarded = 0,

    /// <summary>
    /// The owner is the local pod (cluster + pod match
    /// <see cref="IClusterIdentity"/>). Caller should process in-process
    /// instead of forwarding; this is a defensive short-circuit, not a
    /// configuration error.
    /// </summary>
    LocalOwner = 1,

    /// <summary>
    /// The owning pod could not be reached after the configured retry
    /// budget (DNS, connection, or transport failure). Caller should drop
    /// to the reaper / local-fallback path per ADR-0011.
    /// </summary>
    OwnerUnreachable = 2,

    /// <summary>
    /// The owning pod returned a non-success status after the retry budget
    /// (4xx not-found / 5xx persistent). Caller should drop to the reaper
    /// path; the call is unlikely to be recoverable on this attempt.
    /// </summary>
    RemoteRejected = 3,

    /// <summary>
    /// Cross-cluster forwarding was requested but is not supported per
    /// ADR-0011. Caller should drop to the reaper / local-fallback path
    /// and surface the misconfiguration in logs / telemetry.
    /// </summary>
    CrossClusterBlocked = 4,
}

/// <summary>
/// Result of a single <see cref="IWebhookForwarder.TryForwardAsync"/>.
/// </summary>
/// <param name="Outcome">Disposition of the forward attempt.</param>
/// <param name="StatusCode">HTTP status from the owning pod when the request reached it; <c>null</c> when no response was received.</param>
public readonly record struct WebhookForwardResult(
    WebhookForwardOutcome Outcome,
    int? StatusCode)
{
    /// <summary>Indicates the upstream publisher can be acknowledged.</summary>
    public bool IsSuccess => Outcome == WebhookForwardOutcome.Forwarded;
}
