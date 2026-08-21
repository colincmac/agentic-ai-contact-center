using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Materializes a call-scoped workflow from process-shared compiled metadata.
/// </summary>
public sealed class WorkflowRuntimeBinder
{
    private readonly IIvrToolRegistry _toolRegistry;
    private readonly INamedEdgePredicateProvider _namedPredicates;

    public WorkflowRuntimeBinder(
        IIvrToolRegistry toolRegistry,
        INamedEdgePredicateProvider namedPredicates)
    {
        _toolRegistry = toolRegistry;
        _namedPredicates = namedPredicates;
    }

    public CompiledCallWorkflow Bind(CompiledCallWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var errors = new List<string>();
        var stages = new List<CompiledStage>(workflow.Stages.Count);

        foreach (var stage in workflow.Stages)
        {
            var edges = BindEdges(stage, errors);
            var tools = BindTools(stage, errors);
            stages.Add(new CompiledStage(
                stage.Blueprint,
                edges,
                stage.ToolNames,
                tools,
                stage.BasePrompt));
        }

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException(workflow.Id, errors);
        }

        return new CompiledCallWorkflow(workflow.Blueprint, stages);
    }

    private IReadOnlyList<CompiledStageEdge> BindEdges(
        CompiledStage stage,
        List<string> errors)
    {
        var edges = new List<CompiledStageEdge>(stage.OutgoingEdges.Count);
        foreach (var edge in stage.OutgoingEdges)
        {
            try
            {
                var predicate = WorkflowGraphCompiler.BuildPredicateForTransition(
                    edge.Blueprint,
                    _namedPredicates)
                    ?? throw new InvalidOperationException("Predicate binding produced no runtime predicate.");
                edges.Add(new CompiledStageEdge(edge.Blueprint, predicate));
            }
            catch (Exception ex)
            {
                errors.Add($"Stage '{stage.Id}' transition to '{edge.TargetStageId}': {ex.Message}");
            }
        }
        return edges;
    }

    private IReadOnlyList<AITool> BindTools(
        CompiledStage stage,
        List<string> errors)
    {
        var tools = new List<AITool>(stage.ToolNames.Count);
        foreach (var name in stage.ToolNames)
        {
            if (_toolRegistry.TryGetBinding(name, out var binding) && binding is not null)
            {
                tools.Add(binding);
            }
            else
            {
                errors.Add(
                    $"Stage '{stage.Id}' references unknown tool '{name}'. " +
                    "Register it via services.AddIvrTool() or AddTools<TTools>().");
            }
        }
        return tools;
    }
}
