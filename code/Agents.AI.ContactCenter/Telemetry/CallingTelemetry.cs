using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Telemetry;

/// <summary>
/// Singleton telemetry surface for the Calling/Proposed contact-center stack.
/// Owns the dedicated <see cref="ActivitySource"/> and <see cref="Meter"/> and
/// exposes all instruments + span helpers used by the session, edges, strategies,
/// observers, and the quality reporter.
/// </summary>
public sealed class CallingTelemetry : IDisposable
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;

    // --- Lifecycle counters --------------------------------------------------
    private readonly Counter<long> _callsCreated;
    private readonly Counter<long> _callsStarted;
    private readonly Counter<long> _callsEnded;
    private readonly Counter<long> _callsFaulted;
    private readonly UpDownCounter<int> _activeCalls;
    private readonly Histogram<double> _callDuration;
    private readonly Histogram<double> _timeToFirstAudio;
    private readonly Counter<long> _stateTransitions;
    private readonly Histogram<double> _stateTransitionLatency;

    // --- Supervisor ----------------------------------------------------------
    private readonly Counter<long> _supervisorAttached;
    private readonly Counter<long> _supervisorDetached;
    private readonly Counter<long> _supervisorModeChanged;
    private readonly UpDownCounter<int> _activeSupervisors;

    // --- Transfer / hangup ---------------------------------------------------
    private readonly Counter<long> _transfers;
    private readonly Counter<long> _hangups;

    // --- Edge / dispatch -----------------------------------------------------
    private readonly Counter<long> _inboundAudioFrames;
    private readonly Counter<long> _inboundDtmfTones;
    private readonly Counter<long> _outboundDirectives;
    private readonly Counter<long> _directiveDispatchFailures;
    private readonly Counter<long> _directivesUnsupported;
    private readonly Histogram<double> _dispatchLatency;
    private readonly Counter<long> _edgeConnects;
    private readonly Counter<long> _edgeDisconnects;

    // --- Strategy / events ---------------------------------------------------
    private readonly Counter<long> _strategyEvents;
    private readonly Counter<long> _strategyFaults;
    private readonly Counter<long> _tierDegradations;
    private readonly Counter<long> _dispatchUnsupportedEvents;

    // --- Observers / dashboard / quality -------------------------------------
    private readonly Counter<long> _snapshotUpdates;
    private readonly Counter<long> _alertsRaised;
    private readonly Counter<long> _alertsResolved;

    public CallingTelemetry()
    {
        _activitySource = new ActivitySource(CallingActivitySource.ActivitySourceName);
        _meter = new Meter(CallingActivitySource.MeterName);
        _callsCreated = _meter.CreateCounter<long>(
            "contact_center.call.created",
            description: "Number of call sessions created");
        _callsStarted = _meter.CreateCounter<long>(
            "contact_center.call.started",
            description: "Number of call sessions that successfully reached Active state");
        _callsEnded = _meter.CreateCounter<long>(
            "contact_center.call.ended",
            description: "Number of call sessions that ended");
        _callsFaulted = _meter.CreateCounter<long>(
            "contact_center.call.faulted",
            description: "Number of call sessions that transitioned to Faulted state");
        _activeCalls = _meter.CreateUpDownCounter<int>(
            "contact_center.call.active",
            description: "Currently active call sessions");
        _callDuration = _meter.CreateHistogram<double>(
            "contact_center.call.duration",
            unit: "s",
            description: "Duration of completed call sessions");
        _timeToFirstAudio = _meter.CreateHistogram<double>(
            "contact_center.call.time_to_first_audio",
            unit: "ms",
            description: "Latency from session start to first outbound audio/directive");
        _stateTransitions = _meter.CreateCounter<long>(
            "contact_center.call.state_transitions",
            description: "State transitions executed on a call session");
        _stateTransitionLatency = _meter.CreateHistogram<double>(
            "contact_center.call.state_transition.latency",
            unit: "ms",
            description: "Wall-clock time elapsed between consecutive call-session states");

        _supervisorAttached = _meter.CreateCounter<long>(
            "contact_center.supervisor.attached",
            description: "Supervisor edge attach events");
        _supervisorDetached = _meter.CreateCounter<long>(
            "contact_center.supervisor.detached",
            description: "Supervisor edge detach events");
        _supervisorModeChanged = _meter.CreateCounter<long>(
            "contact_center.supervisor.mode_changed",
            description: "Supervisor mode transitions");
        _activeSupervisors = _meter.CreateUpDownCounter<int>(
            "contact_center.supervisor.active",
            description: "Currently attached supervisors");

        _transfers = _meter.CreateCounter<long>(
            "contact_center.call.transfers",
            description: "Transfer requests initiated");
        _hangups = _meter.CreateCounter<long>(
            "contact_center.call.hangups",
            description: "Hang-up directives issued");

        _inboundAudioFrames = _meter.CreateCounter<long>(
            "contact_center.edge.audio.inbound_frames",
            description: "Inbound audio frames received from an edge");
        _inboundDtmfTones = _meter.CreateCounter<long>(
            "contact_center.edge.dtmf.inbound_tones",
            description: "Inbound DTMF tones received from an edge");
        _outboundDirectives = _meter.CreateCounter<long>(
            "contact_center.edge.directives.dispatched",
            description: "Outbound directives dispatched to an edge");
        _directiveDispatchFailures = _meter.CreateCounter<long>(
            "contact_center.edge.directives.failed",
            description: "Outbound directive dispatch failures");
        _directivesUnsupported = _meter.CreateCounter<long>(
            "contact_center.edge.directives.unsupported",
            description: "Directives dropped because the edge does not support them");
        _dispatchLatency = _meter.CreateHistogram<double>(
            "contact_center.edge.dispatch.latency",
            unit: "ms",
            description: "Per-directive dispatch latency");
        _edgeConnects = _meter.CreateCounter<long>(
            "contact_center.edge.connect",
            description: "Edge connect events");
        _edgeDisconnects = _meter.CreateCounter<long>(
            "contact_center.edge.disconnect",
            description: "Edge disconnect events");

        _strategyEvents = _meter.CreateCounter<long>(
            "contact_center.strategy.events",
            description: "Strategy events emitted, tagged by event kind");
        _strategyFaults = _meter.CreateCounter<long>(
            "contact_center.strategy.faults",
            description: "Strategy fault events emitted");
        _tierDegradations = _meter.CreateCounter<long>(
            "contact_center.strategy.tier_degradations",
            description: "Strategy tier degradations");
        _dispatchUnsupportedEvents = _meter.CreateCounter<long>(
            "contact_center.strategy.dispatch_unsupported",
            description: "DispatchUnsupported events emitted by the session for strategy/edge mismatches");

        _snapshotUpdates = _meter.CreateCounter<long>(
            "contact_center.quality.snapshot_updates",
            description: "CallQualitySnapshot mutations broadcast");
        _alertsRaised = _meter.CreateCounter<long>(
            "contact_center.quality.alerts.raised",
            description: "Quality alerts raised, tagged by kind and severity");
        _alertsResolved = _meter.CreateCounter<long>(
            "contact_center.quality.alerts.resolved",
            description: "Quality alerts resolved, tagged by kind");
    }

    /// <summary>The underlying <see cref="ActivitySource"/>. Exposed for advanced scenarios.</summary>
    public ActivitySource ActivitySource => _activitySource;

    // =====================================================================
    // Span helpers
    // =====================================================================

    public Activity? StartCallActivity(string callId, AgentTier tier, StrategyKind strategyKind)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        var activity = _activitySource.StartActivity(
            CallingActivitySource.CallActivityName,
            ActivityKind.Server);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CallingActivitySource.CallIdTag, callId);
        activity.SetTag(CallingActivitySource.CallTierTag, tier.ToString());
        activity.SetTag(CallingActivitySource.CallStrategyKindTag, strategyKind.ToString());
        return activity;
    }

    public Activity? StartChildActivity(string name, string callId)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        var activity = _activitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag(CallingActivitySource.CallIdTag, callId);
        return activity;
    }

    public Activity? StartStrategyEventActivity(string callId, StrategyEvent ev)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        var activity = _activitySource.StartActivity(
            CallingActivitySource.StrategyEventActivityName,
            ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CallingActivitySource.CallIdTag, callId);
        activity.SetTag(CallingActivitySource.StrategyEventKindTag, ev.GetType().Name);
        switch (ev)
        {
            case StrategyEvent.Transcript t:
                activity.SetTag("strategy.transcript.speaker", t.Speaker);
                activity.SetTag("strategy.transcript.final", t.IsFinal);
                break;
            case StrategyEvent.AgentUtterance u:
                activity.SetTag("strategy.agent.id", u.AgentId);
                break;
            case StrategyEvent.AgentSpeakingChanged s:
                activity.SetTag("strategy.agent.id", s.AgentId);
                activity.SetTag("strategy.agent.display_name", s.AgentDisplayName);
                break;
            case StrategyEvent.DelegateInsight d:
                activity.SetTag("strategy.delegate.agent_id", d.AgentId);
                activity.SetTag("strategy.delegate.confidence", d.Confidence);
                break;
            case StrategyEvent.FunctionCalled f:
                activity.SetTag("strategy.function.name", f.Name);
                break;
            case StrategyEvent.DtmfRecognized dtmf:
                activity.SetTag("strategy.dtmf.digits", dtmf.Digits);
                activity.SetTag("strategy.workflow.step_id", dtmf.StepId);
                break;
            case StrategyEvent.WorkflowStepEntered step:
                activity.SetTag("strategy.workflow.step_id", step.StepId);
                break;
            case StrategyEvent.IntentClassified intent:
                activity.SetTag("strategy.intent.label", intent.Intent);
                activity.SetTag("strategy.intent.confidence", intent.Confidence);
                break;
            case StrategyEvent.EscalationRequested esc:
                activity.SetTag("strategy.escalation.reason", esc.Reason);
                break;
            case StrategyEvent.TierDegraded td:
                activity.SetTag("strategy.tier.from", td.From.ToString());
                activity.SetTag("strategy.tier.to", td.To.ToString());
                activity.SetTag("strategy.tier.reason", td.Reason);
                break;
            case StrategyEvent.Faulted fault:
                activity.SetTag("strategy.fault.message", fault.Message);
                if (fault.Exception is not null)
                {
                    CallingActivitySource.SetError(activity, fault.Exception);
                }
                else
                {
                    activity.SetStatus(ActivityStatusCode.Error, fault.Message);
                }
                break;
            case StrategyEvent.DispatchUnsupported du:
                activity.SetTag(CallingActivitySource.DirectiveKindTag, du.DirectiveKind);
                activity.SetTag(CallingActivitySource.EdgeCapabilitiesTag, du.EdgeCapabilities.ToString());
                activity.SetStatus(ActivityStatusCode.Error, "directive not supported by edge");
                break;
        }
        return activity;
    }

    // =====================================================================
    // Metric helpers (keep tag construction in one place)
    // =====================================================================

    public void CallCreated(string callId, AgentTier tier, StrategyKind strategy)
        => _callsCreated.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallStrategyKindTag, strategy.ToString()));

    public void CallStarted(string callId, AgentTier tier)
    {
        _callsStarted.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()));
        _activeCalls.Add(1);
    }

    public void CallEnded(string callId, AgentTier tier, CallSessionState finalState, string? reason, TimeSpan duration)
    {
        _callsEnded.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateTag, finalState.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallEndReasonTag, reason ?? "unspecified"));
        _activeCalls.Add(-1);
        _callDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateTag, finalState.ToString()));
    }

    public void CallFaulted(string callId, AgentTier tier, string reason)
        => _callsFaulted.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallEndReasonTag, reason));

    public void StateTransition(string callId, CallSessionState from, CallSessionState to, TimeSpan elapsed)
    {
        _stateTransitions.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateFromTag, from.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateToTag, to.ToString()));
        _stateTransitionLatency.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateFromTag, from.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.CallStateToTag, to.ToString()));
    }

    public void RecordTimeToFirstAudio(string callId, AgentTier tier, TimeSpan elapsed)
        => _timeToFirstAudio.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(CallingActivitySource.CallTierTag, tier.ToString()));

    public void SupervisorAttached(string callId, SupervisorMode mode)
    {
        _supervisorAttached.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.SupervisorModeTag, mode.ToString()));
        _activeSupervisors.Add(1);
    }

    public void SupervisorDetached(string callId, SupervisorMode? lastMode)
    {
        _supervisorDetached.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.SupervisorModeTag, lastMode?.ToString() ?? "Unknown"));
        _activeSupervisors.Add(-1);
    }

    public void SupervisorModeChanged(string callId, SupervisorMode? from, SupervisorMode to)
        => _supervisorModeChanged.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.SupervisorModeFromTag, from?.ToString() ?? "None"),
            new KeyValuePair<string, object?>(CallingActivitySource.SupervisorModeToTag, to.ToString()));

    public void TransferInitiated(string callId, TransferKind kind)
        => _transfers.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.TransferKindTag, kind.ToString()));

    public void HangupIssued(string callId, bool hangUpForEveryone, string? reason)
        => _hangups.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.HangupForEveryoneTag, hangUpForEveryone),
            new KeyValuePair<string, object?>(CallingActivitySource.HangupReasonTag, reason ?? "unspecified"));

    public void InboundAudioFrame(string edgeId)
        => _inboundAudioFrames.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId));

    public void InboundDtmfTone(string edgeId)
        => _inboundDtmfTones.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId));

    public void DirectiveDispatched(string edgeId, string directiveKind, TimeSpan elapsed)
    {
        var edgeTag = new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId);
        var kindTag = new KeyValuePair<string, object?>(CallingActivitySource.DirectiveKindTag, directiveKind);
        _outboundDirectives.Add(1, edgeTag, kindTag);
        _dispatchLatency.Record(elapsed.TotalMilliseconds, edgeTag, kindTag);
    }

    public void DirectiveDispatchFailed(string edgeId, string directiveKind, Exception ex)
        => _directiveDispatchFailures.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId),
            new KeyValuePair<string, object?>(CallingActivitySource.DirectiveKindTag, directiveKind),
            new KeyValuePair<string, object?>("error.type", ex.GetType().FullName));

    public void DirectiveUnsupported(string edgeId, string directiveKind, EdgeCapabilities capabilities)
        => _directivesUnsupported.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId),
            new KeyValuePair<string, object?>(CallingActivitySource.DirectiveKindTag, directiveKind),
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeCapabilitiesTag, capabilities.ToString()));

    public void EdgeConnected(string edgeId, CallEdgeKind kind)
        => _edgeConnects.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId),
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeKindTag, kind.ToString()));

    public void EdgeDisconnected(string edgeId, CallEdgeKind kind, EdgeDisconnectedReason reason)
        => _edgeDisconnects.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeIdTag, edgeId),
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeKindTag, kind.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.EdgeDisconnectReasonTag, reason.ToString()));

    public void StrategyEventEmitted(string callId, StrategyEvent ev)
    {
        var kindTag = new KeyValuePair<string, object?>(CallingActivitySource.StrategyEventKindTag, ev.GetType().Name);
        _strategyEvents.Add(1, kindTag);
        switch (ev)
        {
            case StrategyEvent.Faulted:
                _strategyFaults.Add(1, kindTag);
                break;
            case StrategyEvent.TierDegraded td:
                _tierDegradations.Add(1,
                    new KeyValuePair<string, object?>("strategy.tier.from", td.From.ToString()),
                    new KeyValuePair<string, object?>("strategy.tier.to", td.To.ToString()));
                break;
            case StrategyEvent.DispatchUnsupported du:
                _dispatchUnsupportedEvents.Add(1,
                    new KeyValuePair<string, object?>(CallingActivitySource.DirectiveKindTag, du.DirectiveKind),
                    new KeyValuePair<string, object?>(CallingActivitySource.EdgeCapabilitiesTag, du.EdgeCapabilities.ToString()));
                break;
        }
    }

    public void SnapshotUpdated(string callId)
        => _snapshotUpdates.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.CallIdTag, callId));

    public void AlertRaised(string callId, QualityAlert alert)
    {
        _alertsRaised.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.AlertKindTag, alert.Kind.ToString()),
            new KeyValuePair<string, object?>(CallingActivitySource.AlertSeverityTag, alert.Severity.ToString()));

        // Mirror the alert as an activity event on whatever activity is current
        // (typically the long-running call span) so traces show alert timelines.
        CallingActivitySource.RecordAlertEvent(Activity.Current, alert, resolved: false);
    }

    public void AlertResolved(string callId, QualityAlert alert)
    {
        _alertsResolved.Add(1,
            new KeyValuePair<string, object?>(CallingActivitySource.AlertKindTag, alert.Kind.ToString()));
        CallingActivitySource.RecordAlertEvent(Activity.Current, alert, resolved: true);
    }

    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
    }
}

/// <summary>
/// Shared OpenTelemetry attribute-key constants and activity helpers for the
/// Calling/Proposed contact-center stack. Kept in the same file as
/// <see cref="CallingTelemetry"/> so the telemetry surface lives in one place.
/// </summary>
internal static class CallingActivitySource
{
    public const string ActivitySourceName = "Agents.AI.ContactCenter.Calling";
    public const string MeterName = "Agents.AI.ContactCenter.Calling";

    // ---- Call-level tags ----------------------------------------------------
    public const string CallIdTag = "call.id";
    public const string CallTierTag = "call.tier";
    public const string CallStrategyKindTag = "call.strategy.kind";
    public const string CallStateTag = "call.state";
    public const string CallStateFromTag = "call.state.from";
    public const string CallStateToTag = "call.state.to";
    public const string CallEndReasonTag = "call.end.reason";

    // ---- Edge tags ----------------------------------------------------------
    public const string EdgeIdTag = "edge.id";
    public const string EdgeKindTag = "edge.kind";
    public const string EdgeCapabilitiesTag = "edge.capabilities";
    public const string EdgeDisconnectReasonTag = "edge.disconnect.reason";

    // ---- Directive / event tags --------------------------------------------
    public const string DirectiveKindTag = "directive.kind";
    public const string StrategyEventKindTag = "strategy.event.kind";

    // ---- Supervisor tags ----------------------------------------------------
    public const string SupervisorIdTag = "supervisor.id";
    public const string SupervisorModeTag = "supervisor.mode";
    public const string SupervisorModeFromTag = "supervisor.mode.from";
    public const string SupervisorModeToTag = "supervisor.mode.to";

    // ---- Transfer / hangup --------------------------------------------------
    public const string TransferKindTag = "transfer.kind";
    public const string TransferTargetTag = "transfer.target";
    public const string HangupForEveryoneTag = "hangup.for_everyone";
    public const string HangupReasonTag = "hangup.reason";

    // ---- Alert tags ---------------------------------------------------------
    public const string AlertIdTag = "alert.id";
    public const string AlertKindTag = "alert.kind";
    public const string AlertSeverityTag = "alert.severity";
    public const string AlertMessageTag = "alert.message";
    public const string AlertResolvedTag = "alert.resolved";

    // ---- Observer tags ------------------------------------------------------
    public const string ObserverIdTag = "observer.id";

    // ---- Activity names -----------------------------------------------------
    public const string CallActivityName = "contact_center.call";
    public const string CreateSessionActivityName = "contact_center.call.create";
    public const string TransitionActivityName = "contact_center.call.transition";
    public const string TransferActivityName = "contact_center.call.transfer";
    public const string HangupActivityName = "contact_center.call.hangup";
    public const string SupervisorAttachActivityName = "contact_center.call.supervisor.attach";
    public const string SupervisorDetachActivityName = "contact_center.call.supervisor.detach";
    public const string SupervisorModeChangeActivityName = "contact_center.call.supervisor.mode_change";
    public const string ReplaceStrategyActivityName = "contact_center.call.replace_strategy";
    public const string DispatchActivityName = "contact_center.edge.dispatch";
    public const string EdgeConnectActivityName = "contact_center.edge.connect";
    public const string EdgeDisconnectActivityName = "contact_center.edge.disconnect";
    public const string StrategyEventActivityName = "contact_center.strategy.event";

    /// <summary>
    /// Marks <paramref name="activity"/> as failed and attaches <c>error.type</c>
    /// / <c>error.message</c> tags so collectors can fire alerts on them.
    /// </summary>
    public static void SetError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("error.type", ex.GetType().FullName);
        activity.SetTag("error.message", ex.Message);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }

    /// <summary>
    /// Adds a structured event to <paramref name="activity"/> describing a
    /// dual-emitted <see cref="QualityAlert"/>. The same event drives Azure
    /// Monitor / Prometheus alerting rules.
    /// </summary>
    public static void RecordAlertEvent(Activity? activity, QualityAlert alert, bool resolved = false)
    {
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { AlertIdTag, alert.AlertId },
            { AlertKindTag, alert.Kind.ToString() },
            { AlertSeverityTag, alert.Severity.ToString() },
            { AlertMessageTag, alert.Message },
            { AlertResolvedTag, resolved },
        };
        activity.AddEvent(new ActivityEvent(
            name: resolved ? "alert.resolved" : "alert.raised",
            timestamp: alert.RaisedAt,
            tags: tags));
    }
}
