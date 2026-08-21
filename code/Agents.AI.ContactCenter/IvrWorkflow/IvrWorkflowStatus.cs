namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Represents the status of an IVR workflow. Folded onto <c>IvrSnapshot.Status</c> from the
/// <see cref="Calling.StrategyEvent.WorkflowStepEntered"/>, <see cref="Calling.StrategyEvent.EscalationRequested"/>,
/// and <see cref="Calling.StrategyEvent.WorkflowCompleted"/> events.
/// </summary>
public enum IvrWorkflowStatus
{
    /// <summary>Workflow has not started yet.</summary>
    NotStarted,

    /// <summary>Workflow is currently running.</summary>
    Running,

    /// <summary>Workflow is waiting for user input.</summary>
    WaitingForInput,

    /// <summary>A transfer to a human/queue has been requested.</summary>
    TransferRequested,

    /// <summary>Workflow completed successfully.</summary>
    Completed,

    /// <summary>Workflow failed with an error.</summary>
    Failed,

    /// <summary>Workflow was cancelled.</summary>
    Cancelled
}
