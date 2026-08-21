namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>Outcome marker for a terminal stage so the runtime can classify a completed call.</summary>
public enum BlueprintTerminalOutcome
{
    /// <summary>Not a terminal stage.</summary>
    None = 0,
    Success,
    Failure,
    Escalated,
    Abandoned,
}

/// <summary>
/// Authored description of one stage in the call flow. Pure data — no executors, no
/// runtime state. The <see cref="Compilation.WorkflowGraphCompiler"/> resolves tool /
/// predicate references and produces the runtime graph the navigator walks.
/// </summary>
public sealed class StageBlueprint
{
    /// <summary>Stable id referenced by transitions and the host.</summary>
    public required string Id { get; init; }

    /// <summary>Short business description; surfaced to the model's prompt and telemetry.</summary>
    public string? Goal { get; init; }

    /// <summary>Longer business-side description. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>True when entering this stage ends the call.</summary>
    public bool Terminal { get; init; }

    /// <summary>Outcome classification for terminal stages.</summary>
    public BlueprintTerminalOutcome TerminalOutcome { get; init; } = BlueprintTerminalOutcome.None;

    /// <summary>Channel-shaped business config (realtime/NLU/scripted).</summary>
    public StageChannelConfig Channels { get; init; } = new();

    /// <summary>
    /// Optional inline authentication plan. When set, the executor drives the caller through
    /// these credential steps on stage entry before rendering the stage's business prompt.
    /// </summary>
    public AuthenticationPlanBlueprint? Authentication { get; init; }

    /// <summary>Names of additional tools made available while this stage is active (on top of workflow commonTools).</summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>Natural-language hint surfaced to the model describing when to advance out of this stage.</summary>
    public string? ExitCondition { get; init; }

    /// <summary>Outgoing transitions. Ordering is preserved for the realtime "advance" enum.</summary>
    public IReadOnlyList<TransitionBlueprint> Transitions { get; init; } = [];
}
