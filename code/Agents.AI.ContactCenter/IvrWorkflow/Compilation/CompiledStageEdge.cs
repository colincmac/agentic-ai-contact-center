using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Compiled representation of one outgoing edge from a <see cref="CompiledStage"/>.
/// Process-shared metadata retains authored predicate references; a call-scoped runtime copy
/// carries the resolved <see cref="EdgePredicate"/>.
/// </summary>
public sealed class CompiledStageEdge
{
    public CompiledStageEdge(
        TransitionBlueprint blueprint,
        EdgePredicate? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        Blueprint = blueprint;
        Predicate = predicate;
    }

    /// <summary>Authored transition this edge was compiled from.</summary>
    public TransitionBlueprint Blueprint { get; }

    /// <summary>Target stage id.</summary>
    public string TargetStageId => Blueprint.TargetStageId;

    /// <summary>Optional label (defaults to <see cref="TargetStageId"/> when not set).</summary>
    public string Label => Blueprint.Label ?? Blueprint.TargetStageId;

    /// <summary>Authored predicate references retained for runtime binding and diagnostics.</summary>
    public IReadOnlyList<PredicateRef> PredicateReferences => Blueprint.Requires;

    /// <summary>Bound composite predicate, or <see langword="null"/> on process-shared metadata.</summary>
    public EdgePredicate? Predicate { get; }
}
