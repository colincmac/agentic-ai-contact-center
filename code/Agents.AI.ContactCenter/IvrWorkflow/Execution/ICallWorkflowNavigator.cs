using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Walks a <see cref="CompiledCallWorkflow"/> on behalf of a per-tier executor. The
/// navigator owns only the "where are we in the graph" question — prompt rendering and
/// tool resolution are separate responsibilities (handled by the executor).
/// </summary>
/// <remarks>
/// New in Phase 4 — replaces the legacy <c>IIvrWorkflowNavigator</c> for callers that have
/// migrated to <see cref="Blueprint.WorkflowBlueprint"/>. The legacy interface remains in
/// place while the per-tier strategies are flipped over (Phase 5).
/// </remarks>
public interface ICallWorkflowNavigator
{
    /// <summary>The workflow this navigator was created for.</summary>
    CompiledCallWorkflow Workflow { get; }

    /// <summary>The stage the navigator is currently positioned at, or <see langword="null"/> before <see cref="EnterInitialStage"/>.</summary>
    CompiledStage? CurrentStage { get; }

    /// <summary>True once the workflow has reached a terminal stage.</summary>
    bool IsComplete { get; }

    /// <summary>
    /// Enter the workflow's initial stage (or resume from the folded <c>IvrSnapshot.CurrentStepId</c>
    /// when set by a prior tier). Sets <see cref="CurrentStage"/> and returns it.
    /// </summary>
    CompiledStage EnterInitialStage();

    /// <summary>
    /// Evaluate a specific outgoing <paramref name="edge"/> from <see cref="CurrentStage"/>.
    /// Preserves edge identity (label + predicate) so workflows with multiple edges to the
    /// same target stage route deterministically. Does not mutate state — call
    /// <see cref="ApplyTransition"/> with the result's edge to commit.
    /// </summary>
    ValueTask<TransitionEvaluation> EvaluateTransitionAsync(
        CompiledStageEdge edge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit the transition encoded by <paramref name="edge"/>. Sets <see cref="CurrentStage"/>
    /// to <see cref="CompiledStageEdge.TargetStageId"/> (or resolves any further routing on
    /// terminal stages) and returns the new current stage.
    /// </summary>
    CompiledStage ApplyTransition(CompiledStageEdge edge);
}
