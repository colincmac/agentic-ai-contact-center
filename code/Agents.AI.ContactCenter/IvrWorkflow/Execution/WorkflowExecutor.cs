using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>Result returned from <see cref="WorkflowExecutor.AdvanceToAsync"/>.</summary>
public abstract record AdvanceOutcome
{
    /// <summary>Transition succeeded; the workflow is now on <see cref="NewStage"/>.</summary>
    public sealed record Advanced(CompiledStage NewStage) : AdvanceOutcome;

    /// <summary>Transition was denied by a predicate. The current stage is unchanged.</summary>
    public sealed record Denied(string Reason) : AdvanceOutcome;

    /// <summary>The requested target stage does not exist on the current stage's outgoing edges.</summary>
    public sealed record Invalid(string Reason) : AdvanceOutcome;
}

/// <summary>
/// Single-advance API consumed by every per-tier strategy. Delegates routing to the
/// navigator; on a successful transition invokes a tier-supplied render callback so the
/// strategy can push the new stage's prompt + tools onto its underlying transport.
/// </summary>
/// <remarks>
/// Replaces the legacy <c>IvrAdvanceFunctions</c> + per-strategy <c>ApplyStepAsync</c>
/// recursion. The executor is thread-safe: stage transitions are serialized by an
/// internal lock so the realtime tier (where advances may be triggered concurrently by
/// the model and inbound DTMF) keeps the navigator + backend in sync.
/// </remarks>
public sealed class WorkflowExecutor : ICallWorkflowNavigator
{
    private readonly CallWorkflowSession _session;
    private readonly Func<CompiledStage, CancellationToken, ValueTask> _renderStageAsync;
    private readonly Func<AuthStepRender, CancellationToken, ValueTask>? _renderAuthAsync;
    private readonly ChannelWriter<StrategyEvent> _events;
    private readonly Func<CallStateProjector?>? _projectorAccessor;
    private readonly SemaphoreSlim _gate;
    private readonly IReadOnlyDictionary<string, ICredentialAuthenticator> _authenticators;
    private readonly ILogger<WorkflowExecutor> _logger;

    private CompiledStage? _currentStage;
    private int _automaticTransitions;

    public WorkflowExecutor(
        CallWorkflowSession session,
        ChannelWriter<StrategyEvent> events,
        Func<CompiledStage, CancellationToken, ValueTask> renderStageAsync,
        Func<AuthStepRender, CancellationToken, ValueTask>? renderAuthAsync = null,
        Func<CallStateProjector?>? projectorAccessor = null,
        ILogger<WorkflowExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(renderStageAsync);

        _session = session;
        _gate = session.TransitionGate;
        _authenticators = (session.Authenticators ?? []).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        _renderStageAsync = renderStageAsync;
        _renderAuthAsync = renderAuthAsync;
        _events = events;
        _projectorAccessor = projectorAccessor;
        _logger = logger ?? NullLogger<WorkflowExecutor>.Instance;
    }

    /// <summary>The call's state projector, when a state plane is configured; otherwise <see langword="null"/>.</summary>
    private CallStateProjector? Projector => _projectorAccessor?.Invoke();

    /// <summary>Current folded IVR workflow slice (empty when no state plane is configured).</summary>
    private IvrSnapshot Ivr => Projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty;

    /// <summary>Current folded caller-auth slice (empty when no state plane is configured).</summary>
    private AuthSnapshot Auth => Projector?.Get<AuthSnapshot>() ?? AuthSnapshot.Empty;

    public CallWorkflowSession Session => _session;
    public CompiledCallWorkflow Workflow => _session.Workflow;

    public CompiledStage? CurrentStage => _currentStage;
    public bool IsAuthenticating => _currentStage?.AuthenticationPlan is { } plan
        && TryGetNextAuthStep(plan, out _, out _);
    public bool IsComplete => (_currentStage is { Terminal: true } && !IsAuthenticating)
        || Ivr.Status is IvrWorkflowStatus.Completed or IvrWorkflowStatus.Failed or IvrWorkflowStatus.Cancelled;

    /// <summary>
    /// Enter the workflow's initial stage (or resume from prior state) and render it.
    /// </summary>
    public async ValueTask<CompiledStage> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Ivr.WorkflowId is not null && (Ivr.WorkflowId != Workflow.Id || Ivr.WorkflowVersion != Workflow.Version))
            {
                throw new InvalidOperationException("The restored call is pinned to a different workflow revision.");
            }
            if (Ivr.WorkflowId is null)
            {
                await _events.WriteAsync(new StrategyEvent.WorkflowSelected(Workflow.Id, Workflow.Version, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
            EnterInitialStage();
            _automaticTransitions = 0;
            return await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attempt to advance along a specific <paramref name="edge"/> from the current stage.
    /// Preferred over <see cref="AdvanceToAsync(string, CancellationToken)"/> when the caller
    /// already holds the resolved edge (model <c>advance</c> tool, scripted DTMF menu, NLU
    /// intent map) so edge identity — label + predicate — is preserved for stages with
    /// multiple edges to the same target.
    /// </summary>
    public async ValueTask<AdvanceOutcome> AdvanceAlongAsync(
        CompiledStageEdge edge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _automaticTransitions = 0;
            var evaluation = await EvaluateTransitionAsync(edge, cancellationToken)
                .ConfigureAwait(false);

            return await ApplyEvaluationAsync(evaluation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Apply a <see cref="TransitionEvaluation"/> produced under the transition lock: commit
    /// the edge via the navigator and fire the render callback.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private async ValueTask<AdvanceOutcome> ApplyEvaluationAsync(
        TransitionEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        switch (evaluation)
        {
            case TransitionEvaluation.Allowed allowed:
                {
                    ApplyTransition(allowed.Edge);
                    var newStage = await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
                    return new AdvanceOutcome.Advanced(newStage);
                }
            case TransitionEvaluation.Blocked blocked:
                return new AdvanceOutcome.Denied(blocked.Reason);

            case TransitionEvaluation.Invalid invalid:
                return new AdvanceOutcome.Invalid(invalid.Reason);

            default:
                throw new InvalidOperationException(
                    $"Unhandled transition evaluation: {evaluation.GetType().Name}.");
        }
    }


    public CompiledStage EnterInitialStage()
    {
        // Tier swap restoration: if a prior tier already advanced the workflow (folded into the
        // projector's IvrSnapshot, which survives tier swaps and pod failover), resume there.
        // Otherwise start at the blueprint's initial stage. The Running status + current-step pointer
        // are folded from the WorkflowStepEntered event the strategy emits on render — no direct writes.
        var resumeId = Ivr.CurrentStepId;
        if (!string.IsNullOrEmpty(resumeId)
            && Workflow.TryGetStage(resumeId, out var resumed))
        {
            _currentStage = resumed;
            return resumed;
        }

        _currentStage = Workflow.InitialStage;
        return _currentStage;
    }

    public ValueTask<TransitionEvaluation> EvaluateTransitionAsync(
        string targetStageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetStageId);

        var current = _currentStage ?? throw new InvalidOperationException(
            "Navigator has no current stage. Call EnterInitialStage() first.");

        var edge = current.FindEdgeTo(targetStageId);
        if (edge is null)
        {
            return new ValueTask<TransitionEvaluation>(new TransitionEvaluation.Invalid(
                $"Stage '{current.Id}' has no outgoing transition to '{targetStageId}'."));
        }

        return EvaluateTransitionAsync(edge, cancellationToken);
    }

    public async ValueTask<TransitionEvaluation> EvaluateTransitionAsync(
        CompiledStageEdge edge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var current = _currentStage ?? throw new InvalidOperationException(
            "Navigator has no current stage. Call EnterInitialStage() first.");

        if (!current.OutgoingEdges.Contains(edge))
        {
            return new TransitionEvaluation.Invalid("The transition no longer belongs to the active stage.");
        }
        if (Ivr.CurrentStepId is { } activeId && activeId != current.Id)
        {
            return new TransitionEvaluation.Invalid("This executor is no longer on the active call stage.");
        }
        if (IsAuthenticating)
        {
            return new TransitionEvaluation.Blocked(edge, "Required caller verification is incomplete.");
        }
        var context = BuildEdgeContext();
        var predicate = edge.Predicate ?? throw new InvalidOperationException(
            $"Transition '{current.Id}' → '{edge.TargetStageId}' has not been bound to the call scope.");
        var result = await predicate(context, cancellationToken).ConfigureAwait(false);
        if (result.Passed)
        {
            return new TransitionEvaluation.Allowed(edge);
        }

        var reason = result.FailureReason ?? "Transition denied.";

        _logger.LogInformation(
            "Transition '{From}' → '{To}' blocked ({Reason}).",
            current.Id, edge.TargetStageId, reason);
        return new TransitionEvaluation.Blocked(edge, reason);
    }

    public CompiledStage ApplyTransition(CompiledStageEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var target = Workflow.GetStage(edge.TargetStageId);

        // The completed-step + current-step pointer are folded from the WorkflowStepEntered event the
        // strategy emits when it renders the new stage; the terminal status is emitted as WorkflowCompleted
        // by ApplyEvaluationAsync. ApplyTransition only moves the local stage pointer.
        _currentStage = target;
        return target;
    }

    /// <summary>Map a terminal stage's blueprint outcome to the folded <see cref="IvrWorkflowStatus"/>.</summary>
    private static IvrWorkflowStatus TerminalStatus(CompiledStage stage) => stage.Blueprint.TerminalOutcome switch
    {
        BlueprintTerminalOutcome.Success => IvrWorkflowStatus.Completed,
        BlueprintTerminalOutcome.Failure => IvrWorkflowStatus.Failed,
        BlueprintTerminalOutcome.Abandoned => IvrWorkflowStatus.Cancelled,
        BlueprintTerminalOutcome.Escalated => IvrWorkflowStatus.Completed,
        _ => IvrWorkflowStatus.Completed,
    };

    private WorkflowEdgeContext BuildEdgeContext()
    {
        return WorkflowEdgeContext.FromProjector(Projector);
    }

    // ── Inline authentication driver ───────────────────────────────────────────────

    /// <summary>
    /// Render the current stage. If it carries an unsatisfied inline authentication plan, render
    /// the next credential step instead of the business prompt. Failure routes are explicit.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private async ValueTask<CompiledStage> RenderCurrentAsync(CancellationToken cancellationToken)
    {
        var stage = _currentStage ?? throw new InvalidOperationException("No current stage.");
        if (Ivr.CurrentStepId != stage.Id)
        {
            await _events.WriteAsync(new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        if (stage.AuthenticationPlan is { } plan
            && TryGetNextAuthStep(plan, out var stepIndex, out var group))
        {
            if (Projector is null || _renderAuthAsync is null || IsGroupExhausted(plan, group))
            {
                return await FailAuthenticationAsync(stage, "Verification unavailable or maximum attempts exceeded.", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var render = await BuildAuthStepRenderAsync(stage, stepIndex, group, cancellationToken).ConfigureAwait(false);
                if (render is not null)
                {
                    try
                    {
                        await _renderAuthAsync!(render, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CredentialCaptureUnavailableException)
                    {
                        return await FailAuthenticationAsync(stage, "Credential capture is unavailable.", cancellationToken).ConfigureAwait(false);
                    }
                    return stage;
                }
                return await FailAuthenticationAsync(stage, "No eligible verification method is available.", cancellationToken).ConfigureAwait(false);
            }
        }

        if (stage.Blueprint.Action is not null)
        {
            if (++_automaticTransitions > 32) { throw new InvalidOperationException("Automatic action transitions exceeded the per-input limit."); }
            var result = await _session.Services.GetRequiredService<CallActionDispatcher>()
                .ExecuteAsync(_session, stage.Id, cancellationToken).ConfigureAwait(false);
            await _events.WriteAsync(new StrategyEvent.WorkflowActionCompleted(stage.Blueprint.Action, stage.Id,
                result.Succeeded, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            _currentStage = Workflow.GetStage((result.Succeeded ? stage.Blueprint.OnActionSuccess : stage.Blueprint.OnActionFailure)
                ?? throw new InvalidOperationException("Action outcome route is missing."));
            return await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        await _renderStageAsync(stage, cancellationToken).ConfigureAwait(false);
        if (stage.Terminal)
        {
            await _events.WriteAsync(new StrategyEvent.WorkflowCompleted(TerminalStatus(stage), DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        return stage;
    }

    public async Task FailAuthenticationAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FailAuthenticationAsync(_currentStage ?? throw new InvalidOperationException("No current stage."),
                reason, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task HandleInputFailureAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _automaticTransitions = 0;
            var stage = _currentStage ?? throw new InvalidOperationException("No current stage.");
            if (IsAuthenticating)
            {
                await FailAuthenticationAsync(stage, "Input collection failed.", cancellationToken).ConfigureAwait(false);
                return;
            }
            _currentStage = Workflow.GetStage(stage.Blueprint.OnInputFailure
                ?? throw new InvalidOperationException($"Stage '{stage.Id}' has no input-failure route."));
            await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<CompiledStage> FailAuthenticationAsync(CompiledStage stage, string reason, CancellationToken ct)
    {
        var targetId = stage.AuthenticationPlan?.FailureStageId
            ?? throw new InvalidOperationException($"Stage '{stage.Id}' has no authentication failure route.");
        var target = Workflow.GetStage(targetId);
        if (target.Id == stage.Id || target.AuthenticationPlan is not null)
        {
            throw new InvalidOperationException("Authentication failure must route to a different unprotected stage.");
        }
        _logger.LogWarning("Caller verification failed on stage {Stage}; routing to {Target}.", stage.Id, target.Id);
        await _events.WriteAsync(new StrategyEvent.CallerAuthenticationFailed("workflow", reason, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        _currentStage = target;
        return await RenderCurrentAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Submit a credential the active strategy collected for <paramref name="authenticatorName"/>:
    /// stash it on the authenticator's attempt buffer, dispatch the authenticator, record progress,
    /// then re-render (advancing to the next auth step or the business stage).
    /// </summary>
    public async Task SubmitCredentialAsync(
        string authenticatorName,
        CredentialInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatorName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stage = _currentStage ?? throw new InvalidOperationException("No current stage.");
            if (stage.AuthenticationPlan is not { } plan
                || (Ivr.CurrentStepId is { } activeId && activeId != stage.Id)
                || !TryGetNextAuthStep(plan, out _, out var group)
                || !group.AuthenticatorNames.Contains(authenticatorName, StringComparer.OrdinalIgnoreCase)
                || !_authenticators.TryGetValue(authenticatorName, out var authenticator))
            {
                throw new InvalidOperationException("Credential submission does not match the active verification step.");
            }
            if (Auth.GetRequirement(authenticatorName).Attempts >= plan.MaxAttemptsPerStep)
            {
                await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var request = authenticator.DescribeRequest(BuildAuthContext());
            if (input.Value is null || input.Value.Length < (request.MinLength ?? 1)
                || input.Value.Length > (request.MaxLength ?? 256)
                || (request.Kind is CredentialKind.Digits or CredentialKind.OutOfBandCode
                    && input.Value.Any(c => c is < '0' or > '9')))
            {
                await _events.WriteAsync(new StrategyEvent.CredentialAttempted(authenticatorName, false,
                    "Invalid credential format.", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            authenticator.StashInput(BuildAuthContext(), input);

            var run = await DispatchAsync(authenticatorName, cancellationToken).ConfigureAwait(false);
            var step = run.Steps.LastOrDefault(
                s => string.Equals(s.AuthenticatorName, authenticatorName, StringComparison.OrdinalIgnoreCase));
            var satisfied = step?.Outcome is AuthenticationOutcome.Authenticated;
            var reason = step?.Outcome switch
            {
                AuthenticationOutcome.Failed f => f.Reason,
                AuthenticationOutcome.NotApplicable na => na.Reason,
                _ => null,
            };
            await _events.WriteAsync(
                new StrategyEvent.CredentialAttempted(authenticatorName, satisfied, reason, DateTimeOffset.UtcNow,
                    satisfied ? Auth.UserId : null),
                cancellationToken).ConfigureAwait(false);

            if (step?.Outcome is AuthenticationOutcome.NotApplicable)
            {
                await FailAuthenticationAsync(stage, "Verification method unavailable.", cancellationToken).ConfigureAwait(false);
                return;
            }
            await RenderCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetNextAuthStep(AuthenticationPlanBlueprint plan, out int index, out AuthStepGroup group)
    {
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (!CallerEvidencePolicy.Satisfies(Auth, plan.Steps[i], plan.EvidenceMaxAge, DateTimeOffset.UtcNow))
            {
                index = i;
                group = plan.Steps[i];
                return true;
            }
        }
        index = -1;
        group = null!;
        return false;
    }

    private bool IsGroupExhausted(AuthenticationPlanBlueprint plan, AuthStepGroup group)
        => group.AuthenticatorNames.All(n => Auth.GetRequirement(n).Attempts >= plan.MaxAttemptsPerStep);

    private async ValueTask<AuthStepRender?> BuildAuthStepRenderAsync(
        CompiledStage stage,
        int stepIndex,
        AuthStepGroup group,
        CancellationToken cancellationToken)
    {
        var context = BuildAuthContext();
        var requests = new List<CredentialRequest>(group.AuthenticatorNames.Count);
        AuthenticationChallenge? challenge = null;

        foreach (var name in group.AuthenticatorNames)
        {
            if (!_authenticators.TryGetValue(name, out var auth))
            {
                throw new InvalidOperationException($"Stage '{stage.Id}' references unknown authenticator '{name}'.");
            }
            if (Auth.Level < auth.RequiredPriorLevel
                || Auth.GetRequirement(name).Attempts >= stage.AuthenticationPlan!.MaxAttemptsPerStep) { continue; }

            var request = auth.DescribeRequest(context);

            // Out-of-band (OTP): issue the challenge now so the caller has a code to read back.
            // Auto-issue only for a single-authenticator step; an anyOf defers issuance until chosen.
            if (request.OutOfBand && Auth.PendingChallenge is { } pending && pending.ExpiresAt > DateTimeOffset.UtcNow)
            {
                challenge = new AuthenticationChallenge(pending.Method, pending.Prompt, pending.ChallengeId, pending.ExpiresAt);
            }
            else if (request.OutOfBand)
            {
                var run = await DispatchAsync(name, cancellationToken).ConfigureAwait(false);
                var last = run.Steps.LastOrDefault(
                    s => string.Equals(s.AuthenticatorName, name, StringComparison.OrdinalIgnoreCase));
                if (last?.Outcome is AuthenticationOutcome.NeedsChallenge needs)
                {
                    challenge = needs.Challenge;
                }
                else { continue; }
            }

            requests.Add(request);
        }

        return requests.Count > 0 ? new AuthStepRender(stage, stepIndex, requests, challenge) : null;
    }

    private async Task<AuthenticationRunResult> DispatchAsync(string authenticatorName, CancellationToken cancellationToken)
    {
        var dispatcher = _session.CallerElevationDispatcher
            ?? throw new InvalidOperationException("Caller verification requires an elevation dispatcher.");
        return await dispatcher.DispatchAsync(
            authenticatorName, CallId, events: _events, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private AuthenticationContext BuildAuthContext() => new(
        CallId: CallId,
        CallerMetadata: null,
        CurrentIdentity: Auth.Identity,
        Services: _session.Services);

    private string CallId =>
        Projector?.CallId ?? _session.Services.GetService<ICallSessionAccessor>()?.Current?.CallId
        ?? throw new InvalidOperationException("Caller verification requires an active call identity.");

}
