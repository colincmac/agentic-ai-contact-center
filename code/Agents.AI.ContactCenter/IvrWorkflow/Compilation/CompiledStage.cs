using System.Text;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Runtime representation of one stage in a compiled call workflow. Pairs the authored
/// <see cref="StageBlueprint"/> with pre-compiled outgoing edges and the resolved
/// tool surface.
/// </summary>
public sealed class CompiledStage
{
    public CompiledStage(
        StageBlueprint blueprint,
        IReadOnlyList<CompiledStageEdge> outgoingEdges,
        IReadOnlyList<string> toolNames,
        IReadOnlyList<AITool>? stageTools = null,
        string? basePrompt = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(outgoingEdges);
        ArgumentNullException.ThrowIfNull(toolNames);

        Blueprint = blueprint;
        OutgoingEdges = outgoingEdges;
        ToolNames = toolNames;
        StageTools = stageTools ?? [];
        BasePrompt = basePrompt ?? string.Empty;
    }

    public string Id => Blueprint.Id;

    public bool Terminal => Blueprint.Terminal;

    public StageBlueprint Blueprint { get; }

    /// <summary>Inline authentication plan to satisfy on stage entry, or <see langword="null"/> when the stage is unguarded.</summary>
    public AuthenticationPlanBlueprint? AuthenticationPlan => Blueprint.Authentication;

    public IReadOnlyList<CompiledStageEdge> OutgoingEdges { get; }

    public string BasePrompt { get; }

    /// <summary>Authored tool names retained in process-shared compiled metadata.</summary>
    public IReadOnlyList<string> ToolNames { get; }

    /// <summary>
    /// Per-call tool bindings. Empty on process-shared compiled metadata and populated only
    /// on the call-scoped runtime copy.
    /// </summary>
    public IReadOnlyList<AITool> StageTools { get; }

    /// <summary>Find the outgoing edge whose <see cref="CompiledStageEdge.TargetStageId"/> matches; <see langword="null"/> if none.</summary>
    public CompiledStageEdge? FindEdgeTo(string targetStageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetStageId);
        for (var i = 0; i < OutgoingEdges.Count; i++)
        {
            if (string.Equals(OutgoingEdges[i].TargetStageId, targetStageId, StringComparison.Ordinal))
            {
                return OutgoingEdges[i];
            }
        }
        return null;
    }

    /// <summary>Find the outgoing edge by transition label (case-insensitive).</summary>
    public CompiledStageEdge? FindEdgeByLabel(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        for (var i = 0; i < OutgoingEdges.Count; i++)
        {
            if (string.Equals(OutgoingEdges[i].Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return OutgoingEdges[i];
            }
        }
        return null;
    }

    public (List<AITool> tools, string prompt) GetStageToolsAndPrompt(IEnumerable<IPromptStateRenderer>? stateRenderers = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(BasePrompt))
        {
            sb.AppendLine(BasePrompt.Trim());
            sb.AppendLine();
        }

        sb.AppendLine($"# Current Stage: {Id}");
        if (!string.IsNullOrWhiteSpace(Blueprint.Goal))
        {
            sb.AppendLine($"- Goal: {Blueprint.Goal}");
        }
        if (!string.IsNullOrWhiteSpace(Blueprint.Description))
        {
            sb.AppendLine($"- Description: {Blueprint.Description}");
        }
        if (!string.IsNullOrWhiteSpace(Blueprint.ExitCondition))
        {
            sb.AppendLine($"- Exit when: {Blueprint.ExitCondition}");
        }
        sb.AppendLine();

        if (Blueprint.Channels.Realtime is { } realtime)
        {
            if (realtime.Instructions.Count > 0)
            {
                sb.AppendLine("## Instructions");
                foreach (var instruction in realtime.Instructions)
                {
                    sb.AppendLine($"- {instruction}");
                }
                sb.AppendLine();
            }

            if (realtime.Examples.Count > 0)
            {
                sb.AppendLine("## Example utterances");
                foreach (var example in realtime.Examples)
                {
                    sb.AppendLine($"- \"{example}\"");
                }
                sb.AppendLine();
            }
        }

        if (OutgoingEdges.Count > 0 && !Terminal)
        {
            sb.AppendLine("## Available transitions");
            sb.AppendLine("Call `advance` with the matching `target` label when its condition is met:");
            foreach (var edge in OutgoingEdges)
            {
                var hint = !string.IsNullOrWhiteSpace(edge.Blueprint.When)
                    ? $" — {edge.Blueprint.When}"
                    : string.Empty;
                sb.AppendLine($"- `{edge.Label}` → stage `{edge.TargetStageId}`{hint}");
            }
            sb.AppendLine();
        }

        if (stateRenderers?.ToList() is { Count: > 0 } renderers)
        {
            sb.AppendLine("# Current Collected State");

            foreach (var renderer in renderers)
            {
                RenderState(sb, renderer);
            }
        }

        var prompt = sb.ToString().TrimEnd();
        return (StageTools.ToList(), prompt);
    }

    private static void RenderState(StringBuilder sb, IPromptStateRenderer renderer)
    {
        sb.AppendLine(renderer.RenderAsPrompt());
        sb.AppendLine();
    }
}
