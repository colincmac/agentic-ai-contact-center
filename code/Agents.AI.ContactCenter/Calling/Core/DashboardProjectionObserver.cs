using System.Diagnostics;
using Agents.AI.ContactCenter.Telemetry;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Built-in observer that turns <see cref="StrategyEvent"/>s into
/// <see cref="CallQualitySnapshot"/> updates on the dashboard. This is the bare
/// minimum needed for an operator to see any call activity; richer observers
/// (sentiment, presence, recording) plug in alongside it.
/// </summary>
public sealed class DashboardProjectionObserver : ICallObserver
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CallingTelemetry _telemetry;
    private Task? _loop;

    public DashboardProjectionObserver(CallingTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
    }

    public string ObserverId { get; } = $"dashboard-projection";

    public Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        using (var span = _telemetry.StartChildActivity("contact_center.observer.start", observation.CallId))
        {
            span?.SetTag(CallingActivitySource.ObserverIdTag, ObserverId);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _loop = Task.Run(() => RunAsync(observation, _telemetry, linked.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private static async Task RunAsync(CallObservation observation, CallingTelemetry telemetry, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in observation.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Only open a child activity for alert-bearing events to keep span volume sane.
                Activity? span = ev switch
                {
                    StrategyEvent.TierDegraded
                        or StrategyEvent.EscalationRequested
                        or StrategyEvent.Faulted
                        or StrategyEvent.DispatchUnsupported
                        => telemetry.StartStrategyEventActivity(observation.CallId, ev),
                    _ => null
                };

                try
                {
                    switch (ev)
                {
                    case StrategyEvent.AgentUtterance utterance:
                        observation.Quality.Update(current => current with
                        {
                            LatestAgentUtterance = utterance.Text,
                            ActiveSpeakerAgentId = utterance.AgentId
                        });
                        break;

                    case StrategyEvent.Transcript { IsFinal: true } transcript:
                        observation.Quality.Update(current => current with
                        {
                            LatestCallerUtterance = transcript.Text
                        });
                        break;

                    case StrategyEvent.AgentSpeakingChanged speaker:
                        observation.Quality.Update(current => current with
                        {
                            ActiveSpeakerAgentId = speaker.AgentId,
                            ActiveSpeakerDisplayName = speaker.AgentDisplayName
                        });
                        break;

                    case StrategyEvent.WorkflowStepEntered step:
                        observation.Quality.Update(current => current with
                        {
                            CurrentWorkflowStep = step.StepId
                        });
                        break;

                    case StrategyEvent.TierDegraded degraded:
                        observation.Quality.Update(current => current with
                        {
                            ActiveTier = degraded.To
                        });
                        observation.Quality.RaiseAlert(new QualityAlert(
                            AlertId: $"tier-{degraded.At.ToUnixTimeMilliseconds()}",
                            Kind: QualityAlertKind.TierDegraded,
                            Severity: QualityAlertSeverity.Warning,
                            Message: $"Degraded {degraded.From} → {degraded.To}: {degraded.Reason}",
                            RaisedAt: degraded.At));
                        break;

                    case StrategyEvent.EscalationRequested esc:
                        observation.Quality.RaiseAlert(new QualityAlert(
                            AlertId: $"esc-{esc.At.ToUnixTimeMilliseconds()}",
                            Kind: QualityAlertKind.EscalationRequested,
                            Severity: QualityAlertSeverity.Critical,
                            Message: esc.Reason,
                            RaisedAt: esc.At));
                        break;

                    case StrategyEvent.Faulted fault:
                        observation.Quality.RaiseAlert(new QualityAlert(
                            AlertId: $"fault-{fault.At.ToUnixTimeMilliseconds()}",
                            Kind: QualityAlertKind.DelegateAgentFailure,
                            Severity: QualityAlertSeverity.Critical,
                            Message: fault.Message,
                            RaisedAt: fault.At));
                        break;
                    }
                }
                finally
                {
                    span?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
