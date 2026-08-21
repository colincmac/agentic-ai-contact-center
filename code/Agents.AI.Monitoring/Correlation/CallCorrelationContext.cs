namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// OpenTelemetry span/log attribute keys for the canonical call-correlation identifiers.
/// These names are the join keys used by the Grafana / Log Analytics dashboards, so they
/// must stay stable and match the KQL assets under <c>Azure/Queries</c>.
/// </summary>
public static class CallCorrelationTags
{
    /// <summary>Customer-owned canonical id for the whole call (UUIDv7).</summary>
    public const string E2ECallId = "e2e.call_id";

    /// <summary>Durable lookup key carried across the IVR→D365 transfer.</summary>
    public const string ContextId = "call.context_id";

    /// <summary>ACS server call id (stable, identifies the call).</summary>
    public const string ServerCallId = "acs.server_call_id";

    /// <summary>ACS call-connection id (stable per connection; preferred over correlation id).</summary>
    public const string CallConnectionId = "acs.call_connection_id";

    /// <summary>ACS correlation id (may change mid-call; kept for support cross-reference).</summary>
    public const string AcsCorrelationId = "acs.correlation_id";

    /// <summary>W3C traceparent of the ACS IncomingCall ingress span, so disjoint later traces
    /// (e.g. the media WebSocket) can add a span link back to ingress.</summary>
    public const string IngressTraceParent = "call.ingress_traceparent";

    /// <summary>Dynamics 365 conversation / work-item id.</summary>
    public const string D365ConversationId = "d365.conversation_id";

    /// <summary>SHA-256 hash of the caller ANI (no raw EUII in telemetry).</summary>
    public const string CallerAniHash = "caller.ani_hash";

    /// <summary>The called DID / DNIS.</summary>
    public const string CalledDid = "call.called_did";

    /// <summary>Target Dynamics workstream.</summary>
    public const string Workstream = "d365.workstream";

    /// <summary>Target Dynamics queue.</summary>
    public const string Queue = "d365.queue";

    /// <summary>Detected/selected caller intent.</summary>
    public const string Intent = "call.intent";

    /// <summary>Caller language code (e.g. en-US).</summary>
    public const string LanguageCode = "call.language";
}

/// <summary>
/// Canonical, transport-agnostic identifiers that stitch a single contact-center call
/// across ACS, the IVR app, and Dynamics 365. Minted once at ACS ingress and carried
/// (via <see cref="ICallCorrelationAccessor"/> + <see cref="ICallCorrelationStore"/>)
/// through the call lifecycle and the IVR→D365 transfer.
/// </summary>
public sealed record CallCorrelationContext
{
    /// <summary>Customer-owned canonical id for the whole call (UUIDv7).</summary>
    public required string E2ECallId { get; init; }

    /// <summary>Durable lookup key carried across the transfer for D365 hydration.</summary>
    public required string ContextId { get; init; }

    /// <summary>ACS server call id — stable, identifies the call.</summary>
    public string? AcsServerCallId { get; init; }

    /// <summary>ACS call-connection id — stable per connection (preferred join key).</summary>
    public string? AcsCallConnectionId { get; init; }

    /// <summary>ACS correlation id — may change mid-call.</summary>
    public string? AcsCorrelationId { get; init; }

    /// <summary>W3C traceparent (<c>Activity.Id</c>) of the IncomingCall ingress span, captured at mint
    /// time so a later, disjoint trace (e.g. the media WebSocket) can add a span link back to ingress.
    /// ACS does not propagate trace context between the webhook and the media-streaming connection.</summary>
    public string? IngressTraceParent { get; init; }

    /// <summary>Dynamics 365 conversation / work-item id (set once known post-transfer).</summary>
    public string? D365ConversationId { get; init; }

    /// <summary>SHA-256 hash of the caller ANI.</summary>
    public string? CallerAniHash { get; init; }

    /// <summary>Called DID / DNIS.</summary>
    public string? CalledDid { get; init; }

    /// <summary>Target Dynamics workstream.</summary>
    public string? Workstream { get; init; }

    /// <summary>Target Dynamics queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Detected/selected caller intent.</summary>
    public string? Intent { get; init; }

    /// <summary>Caller language code (e.g. en-US).</summary>
    public string? LanguageCode { get; init; }

    public string? MediaStreamingUri { get; init; }
    public string? CallBackUri { get; init; }

    /// <summary>UTC timestamp the context was first minted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Mint a fresh context at ACS ingress. <see cref="ContextId"/> defaults to the
    /// <see cref="E2ECallId"/> so the two are joinable when no separate context id is supplied.
    /// </summary>
    public static CallCorrelationContext New(
        string? acsServerCallId = null,
        string? acsCorrelationId = null,
        string? callerAniHash = null,
        string? calledDid = null)
    {
        var id = CorrelationId.NewId();
        return new CallCorrelationContext
        {
            E2ECallId = id,
            ContextId = id,
            AcsServerCallId = acsServerCallId,
            AcsCorrelationId = acsCorrelationId,
            CallerAniHash = callerAniHash,
            CalledDid = calledDid,
        };
    }

    /// <summary>Enumerate the non-null identifiers as OpenTelemetry-ready tag pairs.</summary>
    public IEnumerable<KeyValuePair<string, object?>> ToTags()
    {
        yield return new(CallCorrelationTags.E2ECallId, E2ECallId);
        yield return new(CallCorrelationTags.ContextId, ContextId);
        if (AcsServerCallId is not null) yield return new(CallCorrelationTags.ServerCallId, AcsServerCallId);
        if (AcsCallConnectionId is not null) yield return new(CallCorrelationTags.CallConnectionId, AcsCallConnectionId);
        if (AcsCorrelationId is not null) yield return new(CallCorrelationTags.AcsCorrelationId, AcsCorrelationId);
        if (IngressTraceParent is not null) yield return new(CallCorrelationTags.IngressTraceParent, IngressTraceParent);
        if (D365ConversationId is not null) yield return new(CallCorrelationTags.D365ConversationId, D365ConversationId);
        if (CallerAniHash is not null) yield return new(CallCorrelationTags.CallerAniHash, CallerAniHash);
        if (CalledDid is not null) yield return new(CallCorrelationTags.CalledDid, CalledDid);
        if (Workstream is not null) yield return new(CallCorrelationTags.Workstream, Workstream);
        if (Queue is not null) yield return new(CallCorrelationTags.Queue, Queue);
        if (Intent is not null) yield return new(CallCorrelationTags.Intent, Intent);
        if (LanguageCode is not null) yield return new(CallCorrelationTags.LanguageCode, LanguageCode);
    }
}
