using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Catalog;

/// <summary>
/// Per-call scoped holder for the workflow chosen for the active call. Bound by
/// <c>CallSessionFactory</c> on the call's DI scope before any strategy is built, so every
/// tier factory (and the composite chain that shares the scope) resolves the <em>same</em>
/// workflow without baking a workflow id into the strategy registration.
/// </summary>
/// <remarks>
/// Decouples the two registration axes: strategies/tiers are host capabilities registered
/// once, while workflows are data selected per call. When no workflow id is supplied and
/// exactly one workflow is registered, <see cref="Resolve"/> defaults to it so single-workflow
/// hosts need no routing.
/// </remarks>
public sealed class CallWorkflowSelection
{
    /// <summary>The workflow id chosen for this call, or <see langword="null"/> to use the default.</summary>
    public string? WorkflowId { get; private set; }

    /// <summary>Bind the workflow id for this scope. A <see langword="null"/> value leaves the selection unset.</summary>
    public void Set(string? workflowId) => WorkflowId = workflowId;

    /// <summary>
    /// Resolve the compiled workflow for this call. Precedence: the per-call
    /// <see cref="WorkflowId"/>, then <paramref name="fallbackId"/>, then — if neither is set —
    /// the single registered workflow when the catalog is unambiguous.
    /// </summary>
    /// <param name="catalog">The process-wide compiled-workflow catalog.</param>
    /// <param name="fallbackId">Optional default workflow id bound at strategy registration.</param>
    /// <exception cref="InvalidOperationException">
    /// No workflow id was supplied and the catalog has zero or more than one workflow.
    /// </exception>
    public CompiledCallWorkflow Resolve(ICallWorkflowCatalog catalog, string? fallbackId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var id = WorkflowId ?? fallbackId;
        if (!string.IsNullOrEmpty(id))
        {
            return catalog.Get(id);
        }

        var workflows = catalog.Workflows;
        if (workflows.Count == 1)
        {
            return workflows[0];
        }

        throw new InvalidOperationException(
            workflows.Count == 0
                ? "No call workflows are registered. Register one via services.AddCallWorkflow(...)."
                : $"Multiple call workflows are registered ({string.Join(", ", workflows.Select(w => w.Id))}); " +
                  "specify CallSessionRequest.WorkflowId (or a default workflow id when registering the strategy) to select one.");
    }
}
