namespace Agents.AI.ContactCenter.IvrWorkflow.Catalog;

/// <summary>Process-wide defaults used when a call request does not select a workflow.</summary>
public sealed class CallWorkflowOptions
{
    /// <summary>Default workflow id. A <c>CallSessionRequest.WorkflowId</c> takes precedence.</summary>
    public string? DefaultWorkflowId { get; set; }
}
