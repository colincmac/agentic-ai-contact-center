using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.IvrWorkflow.AgentFramework;

public sealed record CallWorkflowCommand(string? TransitionLabel = null);
public sealed record CallWorkflowCommandResult(string StageId, bool Accepted, string? Reason = null);

/// <summary>
/// Agent Framework adapter for discrete control operations. It never owns a socket or consumes PCM.
/// The host retains the call-scoped executor across commands; MAF checkpoints do not persist the call.
/// </summary>
public sealed class CallWorkflowCommandExecutor(WorkflowExecutor executor, string id = "contact-center-control")
    : Executor<CallWorkflowCommand, CallWorkflowCommandResult>(id)
{
    public override async ValueTask<CallWorkflowCommandResult> HandleAsync(
        CallWorkflowCommand message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (executor.CurrentStage is null)
        {
            await executor.EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        if (message.TransitionLabel is null)
        {
            return new(executor.CurrentStage!.Id, true);
        }
        var edge = executor.CurrentStage!.FindEdgeByLabel(message.TransitionLabel);
        if (edge is null) { return new(executor.CurrentStage.Id, false, "Unknown transition label."); }
        var result = await executor.AdvanceAlongAsync(edge, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AdvanceOutcome.Advanced advanced => new(advanced.NewStage.Id, true),
            AdvanceOutcome.Denied denied => new(executor.CurrentStage.Id, false, denied.Reason),
            AdvanceOutcome.Invalid invalid => new(executor.CurrentStage.Id, false, invalid.Reason),
            _ => throw new InvalidOperationException("Unknown workflow transition outcome."),
        };
    }
}
