using System.Threading.Channels;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Live, mutable view of a call's quality and content. Streamed to operator dashboards.
/// One snapshot per active call; replaces ad-hoc updates against <c>LiveCallSummary</c>.
/// </summary>
/// <remarks>
/// CallQualitySnapshot is the unit the operator dashboard consumes (one snapshot
/// per call, mutated continuously). ICallQualityReporter is what observers call
/// to push updates. The dashboard hub (today's OperatorDashboardBroadcaster) becomes
/// a subscriber to ICallQualityReporter rather than reaching into session internals.
/// </remarks>
public sealed record CallQualitySnapshot
{
    public required string CallId { get; init; }
    public required CallSessionState State { get; init; }
    public required AgentTier ActiveTier { get; init; }
    public required StrategyKind StrategyKind { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public CallerSentiment Sentiment { get; init; } = CallerSentiment.Unknown;

    /// <summary>0..1 — agreement between text sentiment and acoustic emotion.</summary>
    public double SignalAgreement { get; init; }

    /// <summary>0..1 — likelihood the caller will request escalation soon.</summary>
    public double EscalationRisk { get; init; }

    /// <summary>0..1 — overall conversational health (latency, repetition, frustration).</summary>
    public double ConversationHealth { get; init; }

    /// <summary>What the caller heard most recently from the agent.</summary>
    public string? LatestAgentUtterance { get; init; }

    /// <summary>What the caller said most recently.</summary>
    public string? LatestCallerUtterance { get; init; }

    /// <summary>Which agent inside the ensemble is currently speaking, if applicable.</summary>
    public string? ActiveSpeakerAgentId { get; init; }

    public string? ActiveSpeakerDisplayName { get; init; }

    /// <summary>Currently active workflow step or topic.</summary>
    public string? CurrentWorkflowStep { get; init; }

    /// <summary>Tasks that delegate agents are working on right now.</summary>
    public IReadOnlyList<string> DelegateTasks { get; init; } = [];

    /// <summary>Open alerts the dashboard should surface.</summary>
    public IReadOnlyList<QualityAlert> Alerts { get; init; } = [];

    /// <summary>Set when a supervisor is attached.</summary>
    public SupervisorPresence? Supervisor { get; init; }
}

public sealed record CallerSentiment(
    SentimentLabel Label,
    double Score,           // -1..1
    double Confidence)      // 0..1
{
    public static CallerSentiment Unknown { get; } = new(SentimentLabel.Unknown, 0, 0);
}

public enum SentimentLabel
{
    Unknown,
    Positive,
    Neutral,
    Negative,
    Frustrated,
    Angry
}

public sealed record QualityAlert(
    string AlertId,
    QualityAlertKind Kind,
    QualityAlertSeverity Severity,
    string Message,
    DateTimeOffset RaisedAt);

public enum QualityAlertKind
{
    HighFrustration,
    LongSilence,
    RepeatedMisunderstanding,
    DelegateAgentFailure,
    TierDegraded,
    EscalationRequested,
    PolicyViolation,
    SupervisorWhisper
}

public enum QualityAlertSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record SupervisorPresence(
    string SupervisorId,
    string DisplayName,
    SupervisorMode Mode,
    DateTimeOffset AttachedAt);

/// <summary>
/// Pushed to by analytics observers, consumed by the dashboard broadcaster.
/// Decouples "who computes quality" from "who shows it".
/// </summary>
public interface ICallQualityReporter
{
    /// <summary>Register the active quality projection for a call.</summary>
    /// <remarks>
    /// A newer registration for the same call id supersedes the returned handle. Mutations and
    /// disposal through a superseded handle are ignored so stale sessions cannot affect a replacement.
    /// </remarks>
    ICallQualityRegistration Register(CallQualitySnapshot initial);

    /// <summary>Read the current snapshot for a call. Returns null if the call is unknown.</summary>
    CallQualitySnapshot? TryGetSnapshot(string callId);

    /// <summary>Snapshot of every active call's current quality view.</summary>
    IReadOnlyCollection<CallQualitySnapshot> GetActiveSnapshots();

    /// <summary>Subscribe to bounded live snapshots. Dispose the subscription when the consumer disconnects.</summary>
    ICallQualitySubscription Subscribe(string? callIdFilter = null);
}

public interface ICallQualitySubscription : IDisposable
{
    ChannelReader<CallQualitySnapshot> Reader { get; }
}

/// <summary>Identity-bound mutation surface for one active call-quality projection.</summary>
public interface ICallQualityRegistration : IDisposable
{
    string CallId { get; }

    /// <summary>
    /// Atomically patch the live snapshot. The mutator receives the latest snapshot and
    /// returns a new snapshot, typically <c>current with { Field = ... }</c>.
    /// </summary>
    void Update(Func<CallQualitySnapshot, CallQualitySnapshot> mutate);

    void RaiseAlert(QualityAlert alert);

    void ResolveAlert(string alertId);
}

/// <summary>
/// Mutable builder kept for backward-compatible spot updates. Prefer the
/// <c>current with { ... }</c> form passed to <see cref="ICallQualityRegistration.Update"/>.
/// </summary>
public sealed class CallQualitySnapshotBuilder
{
    public CallSessionState? State { get; set; }
    public AgentTier? ActiveTier { get; set; }
    public CallerSentiment? Sentiment { get; set; }
    public double? SignalAgreement { get; set; }
    public double? EscalationRisk { get; set; }
    public double? ConversationHealth { get; set; }
    public string? LatestAgentUtterance { get; set; }
    public string? LatestCallerUtterance { get; set; }
    public string? ActiveSpeakerAgentId { get; set; }
    public string? ActiveSpeakerDisplayName { get; set; }
    public string? CurrentWorkflowStep { get; set; }
    public IReadOnlyList<string>? DelegateTasks { get; set; }
    public SupervisorPresence? Supervisor { get; set; }
}
