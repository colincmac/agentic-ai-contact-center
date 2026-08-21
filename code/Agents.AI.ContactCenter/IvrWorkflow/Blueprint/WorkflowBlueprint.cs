namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>
/// Authored, business-side description of a single self-contained call workflow. Replaces
/// the legacy <c>RealtimeIvrWorkflowDefinition</c> in the new design: blueprints carry the
/// "what" (prompts, tool names, SSML, transitions, business intent) and the compiler
/// produces the runtime "how" (executor wiring, predicate evaluation, graph topology).
/// </summary>
/// <remarks>
/// Workflows are <em>self-contained</em>: no <c>import:</c>, no <c>authResolvers</c>, no
/// runtime subflow stack. Authentication is expressed as an inline
/// <see cref="StageBlueprint.Authentication"/> plan the executor drives on stage entry.
/// </remarks>
public sealed class WorkflowBlueprint
{
    /// <summary>Stable workflow id (used by the catalog and telemetry).</summary>
    public required string Id { get; init; }

    /// <summary>Workflow version. Defaults to 1. Used by the catalog when multiple versions co-exist.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Human-readable description. Surfaced in operator dashboards.</summary>
    public string? Description { get; init; }

    /// <summary>Shared system-prompt prefix injected into every stage prompt the realtime tier renders.</summary>
    public string? BasePrompt { get; init; }

    /// <summary>Tool names that should be available in every stage of this workflow.</summary>
    public IReadOnlyList<string> CommonToolNames { get; init; } = [];

    /// <summary>Id of the stage entered on call start. Must match one of <see cref="Stages"/>.</summary>
    public required string InitialStageId { get; init; }

    /// <summary>Authored stages. Order is preserved for fallback iteration / diagnostics.</summary>
    public required IReadOnlyList<StageBlueprint> Stages { get; init; }
}
