using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Authorization;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Materializes a call-scoped workflow from process-shared compiled metadata.
/// </summary>
public sealed class WorkflowRuntimeBinder
{
    private readonly IIvrToolRegistry _toolRegistry;
    private readonly INamedEdgePredicateProvider _namedPredicates;
    private readonly HashSet<string> _authenticators;
    private readonly IServiceProvider? _services;
    private readonly HashSet<string> _actions;
    private readonly HashSet<string> _profiles;

    public WorkflowRuntimeBinder(
        IIvrToolRegistry toolRegistry,
        INamedEdgePredicateProvider namedPredicates,
        IEnumerable<ICallerAuthenticator>? authenticators = null,
        IServiceProvider? services = null,
        IEnumerable<ICallWorkflowAction>? actions = null,
        IOptions<CallInteractionOptions>? interactionOptions = null)
    {
        _toolRegistry = toolRegistry;
        _namedPredicates = namedPredicates;
        _authenticators = (authenticators ?? []).OfType<ICredentialAuthenticator>()
            .Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _services = services;
        _actions = (actions ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        _profiles = (interactionOptions?.Value.Profiles ?? []).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    public CompiledCallWorkflow Bind(CompiledCallWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var errors = new List<string>();
        var stages = new List<CompiledStage>(workflow.Stages.Count);

        foreach (var stage in workflow.Stages)
        {
            foreach (var profile in stage.Blueprint.InteractionProfiles)
            {
                if (!_profiles.Contains(profile)) { errors.Add($"Stage '{stage.Id}' references unknown interaction profile '{profile}'."); }
            }
            if (stage.Blueprint.Action is { } action && !_actions.Contains(action))
            {
                errors.Add($"Stage '{stage.Id}' references unknown action '{action}'.");
            }
            foreach (var name in stage.AuthenticationPlan?.Steps.SelectMany(g => g.AuthenticatorNames) ?? [])
            {
                if (!_authenticators.Contains(name))
                {
                    errors.Add($"Stage '{stage.Id}' references unknown credential authenticator '{name}'.");
                }
            }
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
                tools.Add(binding is AIFunction function ? new AuthorizedWorkflowFunction(function, stage.Blueprint, _services) : binding);
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
