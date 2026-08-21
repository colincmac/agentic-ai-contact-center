using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Result of <see cref="ICallWorkflowNavigator.EvaluateTransitionAsync(CompiledStageEdge, System.Threading.CancellationToken)"/>.
/// Describes whether a requested transition is permitted, denied, or invalid.
/// </summary>
public abstract record TransitionEvaluation
{
    /// <summary>The transition is allowed; the navigator can commit it via <see cref="ICallWorkflowNavigator.ApplyTransition"/>.</summary>
    public sealed record Allowed(CompiledStageEdge Edge) : TransitionEvaluation;

    /// <summary>
    /// The transition's predicate denied. The caller should surface <see cref="Reason"/> back to the model.
    /// </summary>
    public sealed record Blocked(CompiledStageEdge Edge, string Reason) : TransitionEvaluation;

    /// <summary>No matching edge exists from the current stage to the requested target.</summary>
    public sealed record Invalid(string Reason) : TransitionEvaluation;
}
