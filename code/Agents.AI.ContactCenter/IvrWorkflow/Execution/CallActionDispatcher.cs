using System.Collections.Concurrent;
using System.Security.Claims;
using Agents.AI.ContactCenter.Authorization;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.AspNetCore.Authorization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

public sealed record CallActionContext(
    string IdempotencyKey, string CallId, string WorkflowId, int WorkflowVersion,
    string StageId, CallerIdentity Caller, IReadOnlyDictionary<string, string?> Data);

public sealed record CallActionResult(bool Succeeded, string Outcome);

/// <summary>
/// Backend actions must honor the supplied idempotency key, including across call-worker restarts.
/// They must not return success for an uncertain external outcome.
/// </summary>
public interface ICallWorkflowAction
{
    string Name { get; }
    Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken);
}

public sealed class CallActionDispatcher(
    IEnumerable<ICallWorkflowAction> actions,
    CallStateProjector projector,
    IAuthorizationService authorization)
{
    private readonly IReadOnlyDictionary<string, ICallWorkflowAction> _actions =
        actions.ToDictionary(a => a.Name, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CallActionResult>>> _invocations = new();

    public async Task<CallActionResult> ExecuteAsync(CallWorkflowSession session, string stageId, CancellationToken ct)
    {
        var stage = session.Workflow.GetStage(stageId);
        var actionName = stage.Blueprint.Action
            ?? throw new InvalidOperationException("The stage has no configured action.");
        var auth = projector.Get<AuthSnapshot>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            auth.IsAuthenticated ? [new Claim(ClaimTypes.NameIdentifier, auth.UserId)] : [],
            auth.IsAuthenticated ? "CallerVerification" : null));
        var approved = await authorization.AuthorizeAsync(principal, stage,
            new WorkflowActionRequirement(stage.Blueprint, CallerVerificationLevel.None)).ConfigureAwait(false);
        if (!approved.Succeeded) { throw new UnauthorizedAccessException("The action is not authorized."); }
        var action = _actions.TryGetValue(actionName, out var configured) ? configured
            : throw new InvalidOperationException($"Unknown workflow action '{actionName}'.");
        var key = string.Join(":", new[]
        {
            projector.CallId, session.Workflow.Id,
            session.Workflow.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), stage.Id,
        }.Select(Uri.EscapeDataString));
        var context = new CallActionContext(key, projector.CallId, session.Workflow.Id, session.Workflow.Version,
            stage.Id, auth.Identity, projector.Get<IvrSnapshot>().Slots);
        return await _invocations.GetOrAdd(key, _ => new(() => action.ExecuteAsync(context, ct)))
            .Value.WaitAsync(ct).ConfigureAwait(false);
    }
}
