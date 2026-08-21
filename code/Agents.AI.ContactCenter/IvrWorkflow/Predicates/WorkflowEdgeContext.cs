using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>
/// State snapshot available to an <see cref="EdgePredicate"/> when the workflow runtime evaluates a
/// transition. Carries the immutable <see cref="IvrSnapshot"/> and <see cref="AuthSnapshot"/> folded by
/// the call's <see cref="CallStateProjector"/>.
/// </summary>
/// <remarks>
/// Predicates are pure functions over the per-call state; they read the snapshots and never mutate them.
/// </remarks>
public sealed class WorkflowEdgeContext
{
    public WorkflowEdgeContext(IvrSnapshot workflow, AuthSnapshot auth)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(auth);
        Workflow = workflow;
        Auth = auth;
    }

    /// <summary>Folded IVR workflow state (current step id, completed steps, collected slots, status).</summary>
    public IvrSnapshot Workflow { get; }

    /// <summary>Folded caller-authentication state (identity, verification level, audit trail, requirements).</summary>
    public AuthSnapshot Auth { get; }

    /// <summary>
    /// Build a context from the call's projector. Reads the current <see cref="IvrSnapshot"/> and
    /// <see cref="AuthSnapshot"/>; falls back to the empty snapshots when no state plane is configured.
    /// </summary>
    public static WorkflowEdgeContext FromProjector(CallStateProjector? projector) => new(
        projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty,
        projector?.Get<AuthSnapshot>() ?? AuthSnapshot.Empty);
}
