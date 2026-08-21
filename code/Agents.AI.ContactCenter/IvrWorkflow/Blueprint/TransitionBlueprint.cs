namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>
/// Authored description of a single transition out of a stage. Carries a natural-language
/// label and guard predicates; the compiler folds these into a runtime
/// <see cref="Compilation.CompiledStageEdge"/>.
/// </summary>
public sealed class TransitionBlueprint
{
    /// <summary>Id of the target stage. Must exist on the parent workflow.</summary>
    public required string TargetStageId { get; init; }

    /// <summary>Short label used by the realtime "advance" function enum and for telemetry.</summary>
    public string? Label { get; init; }

    /// <summary>Natural-language hint embedded in the prompt for the LLM (e.g. "Caller wants to hear their balance").</summary>
    public string? When { get; init; }

    /// <summary>Per-edge guard predicates. All must pass (AND) for the transition to be allowed.</summary>
    public IReadOnlyList<PredicateRef> Requires { get; init; } = [];
}
