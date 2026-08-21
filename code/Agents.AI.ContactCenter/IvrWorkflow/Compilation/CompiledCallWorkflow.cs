using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Compiled, runtime-shaped representation of a <see cref="WorkflowBlueprint"/>. Carries
/// pre-built <see cref="CompiledStage"/> instances (with pre-compiled
/// <see cref="CompiledStageEdge.Predicate"/> closures) and a stage-id lookup. Consumed by
/// the workflow navigator (Phase 4) and the per-tier strategies (Phase 5).
/// </summary>
/// <remarks>
/// A <see cref="CompiledCallWorkflow"/> is immutable and process-shared. Per-call mutable
/// state (current stage id, collected data, transcript) lives in <c>IvrWorkflowState</c>.
/// </remarks>
public sealed class CompiledCallWorkflow
{
    private readonly IReadOnlyDictionary<string, CompiledStage> _stagesById;

    internal CompiledCallWorkflow(
        WorkflowBlueprint blueprint,
        IReadOnlyList<CompiledStage> stages)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(stages);

        Blueprint = blueprint;
        Stages = stages;
        _stagesById = stages.ToDictionary(s => s.Id, StringComparer.Ordinal);
        InitialStage = _stagesById.TryGetValue(blueprint.InitialStageId, out var initial)
            ? initial
            : throw new InvalidOperationException(
                $"Initial stage '{blueprint.InitialStageId}' not present in compiled workflow '{blueprint.Id}'.");
    }

    /// <summary>Original blueprint this was compiled from.</summary>
    public WorkflowBlueprint Blueprint { get; }

    public string Id => Blueprint.Id;

    public int Version => Blueprint.Version;

    public CompiledStage InitialStage { get; }

    public IReadOnlyList<CompiledStage> Stages { get; }

    public bool TryGetStage(string id, out CompiledStage stage)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _stagesById.TryGetValue(id, out stage!);
    }

    public CompiledStage GetStage(string id) =>
        TryGetStage(id, out var stage)
            ? stage
            : throw new KeyNotFoundException($"No stage '{id}' in workflow '{Id}'.");
}
