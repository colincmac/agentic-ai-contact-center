using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Runtime representation of one outgoing edge from a <see cref="CompiledStage"/>. Holds
/// the pre-resolved <see cref="EdgePredicate"/> (built by <see cref="WorkflowGraphCompiler"/>
/// from the blueprint's <see cref="PredicateRef"/> entries) plus the original blueprint
/// for diagnostics and prompt rendering.
/// </summary>
public sealed class CompiledStageEdge
{
    public CompiledStageEdge(
        TransitionBlueprint blueprint,
        EdgePredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(predicate);

        Blueprint = blueprint;
        Predicate = predicate;
    }

    /// <summary>Authored transition this edge was compiled from.</summary>
    public TransitionBlueprint Blueprint { get; }

    /// <summary>Target stage id.</summary>
    public string TargetStageId => Blueprint.TargetStageId;

    /// <summary>Optional label (defaults to <see cref="TargetStageId"/> when not set).</summary>
    public string Label => Blueprint.Label ?? Blueprint.TargetStageId;

    /// <summary>Composite predicate (AND of every entry in <see cref="TransitionBlueprint.Requires"/>).</summary>
    public EdgePredicate Predicate { get; }
}
