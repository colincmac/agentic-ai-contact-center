using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Translates a <see cref="WorkflowBlueprint"/> into a runtime <see cref="CompiledCallWorkflow"/>.
/// Resolves every <see cref="PredicateRef"/> against the built-in factories (and, for
/// <see cref="PredicateKind.Named"/>, the supplied <see cref="INamedEdgePredicateProvider"/>),
/// resolves every blueprint tool name against the supplied <see cref="IIvrToolRegistry"/>,
/// validates graph structure (no duplicate ids, every transition target exists, an initial
/// stage exists), and produces immutable <see cref="CompiledStage"/> nodes with pre-built
/// edge predicates and resolved tool bindings.
/// </summary>
/// <remarks>
/// Tool resolution fails fast: any reference in
/// <see cref="WorkflowBlueprint.CommonToolNames"/>, <see cref="StageBlueprint.ToolNames"/>,
/// or <see cref="StageRealtimePrompt.ToolNames"/> that is not present in the registry is
/// aggregated into the same <see cref="WorkflowCompilationException"/> as structural and
/// predicate errors, so authors see every problem in a single failure. When the compiler
/// is constructed without an <see cref="IIvrToolRegistry"/> the per-stage tool list is left
/// empty and validation is skipped — this mode is intended for tests and greenfield
/// scenarios that do not surface tools.
/// </remarks>
public sealed class WorkflowGraphCompiler
{
    private readonly INamedEdgePredicateProvider? _namedPredicates;
    private readonly IIvrToolRegistry? _toolRegistry;

    /// <summary>Construct a compiler. Pass <paramref name="namedPredicates"/> to enable <see cref="PredicateKind.Named"/> references and <paramref name="toolRegistry"/> to enable tool-name validation.</summary>
    public WorkflowGraphCompiler(
        INamedEdgePredicateProvider? namedPredicates = null,
        IIvrToolRegistry? toolRegistry = null)
    {
        _namedPredicates = namedPredicates;
        _toolRegistry = toolRegistry;
    }

    /// <summary>Compile <paramref name="blueprint"/>. Throws <see cref="WorkflowCompilationException"/> on any validation error.</summary>
    public CompiledCallWorkflow Compile(WorkflowBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        var errors = new List<string>();
        ValidateStructure(blueprint, errors);

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException(blueprint.Id, errors);
        }

        // First pass: hydrate every stage with an empty edge list so we can validate
        // transition targets against it (every target must be a known stage).
        var compiledStages = new Dictionary<string, CompiledStage>(StringComparer.Ordinal);

        foreach (var stage in blueprint.Stages)
        {
            var edges = new List<CompiledStageEdge>(stage.Transitions.Count);

            foreach (var transition in stage.Transitions)
            {
                if (!blueprint.Stages.Any(s => string.Equals(s.Id, transition.TargetStageId, StringComparison.Ordinal)))
                {
                    errors.Add($"Stage '{stage.Id}' transitions to unknown stage '{transition.TargetStageId}'.");
                    continue;
                }

                EdgePredicate predicate;
                try
                {
                    predicate = BuildPredicateForTransition(transition);
                }
                catch (Exception ex)
                {
                    errors.Add($"Stage '{stage.Id}' transition to '{transition.TargetStageId}': {ex.Message}");
                    continue;
                }

                edges.Add(new CompiledStageEdge(transition, predicate));
            }

            var stageTools = ResolveStageTools(blueprint, stage, errors);

            compiledStages[stage.Id] = new CompiledStage(stage, edges, stageTools, blueprint.BasePrompt);
        }

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException(blueprint.Id, errors);
        }

        // Preserve blueprint ordering.
        var orderedStages = blueprint.Stages.Select(s => compiledStages[s.Id]).ToList();
        return new CompiledCallWorkflow(blueprint, orderedStages);
    }

    /// <summary>
    /// Resolve every tool name referenced by <paramref name="blueprint"/>.<see cref="WorkflowBlueprint.CommonToolNames"/>,
    /// <paramref name="stage"/>.<see cref="StageBlueprint.ToolNames"/>, and the stage's
    /// <see cref="StageRealtimePrompt.ToolNames"/>, deduped in author order (last-wins on
    /// collision via dictionary insertion semantics). Missing names append to
    /// <paramref name="errors"/> so a single compilation surfaces every issue.
    /// </summary>
    private IReadOnlyList<AITool> ResolveStageTools(
        WorkflowBlueprint blueprint,
        StageBlueprint stage,
        List<string> errors)
    {
        if (_toolRegistry is null)
        {
            return [];
        }

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(blueprint.CommonToolNames, ordered, seen);
        Collect(stage.ToolNames, ordered, seen);
        if (stage.Channels.Realtime is { ToolNames.Count: > 0 } realtime)
        {
            Collect(realtime.ToolNames, ordered, seen);
        }

        if (ordered.Count == 0)
        {
            return [];
        }

        var resolved = new List<AITool>(ordered.Count);
        foreach (var name in ordered)
        {
            if (_toolRegistry.TryGetBinding(name, out var binding) && binding is { } tool)
            {
                resolved.Add(tool);
            }
            else
            {
                errors.Add(
                    $"Stage '{stage.Id}' references unknown tool '{name}'. " +
                    "Register it via services.AddIvrTool();");
            }
        }
        return resolved;
    }

    private static void Collect(IReadOnlyList<string> names, List<string> ordered, HashSet<string> seen)
    {
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                ordered.Add(name);
            }
        }
    }

    private static void ValidateStructure(WorkflowBlueprint blueprint, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(blueprint.Id))
        {
            errors.Add("Workflow id is required.");
        }
        if (blueprint.Stages.Count == 0)
        {
            errors.Add("Workflow must define at least one stage.");
        }
        if (string.IsNullOrWhiteSpace(blueprint.InitialStageId))
        {
            errors.Add("Workflow must declare an initialStageId.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in blueprint.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                errors.Add("A stage with an empty id is not allowed.");
                continue;
            }
            if (!seen.Add(stage.Id))
            {
                errors.Add($"Duplicate stage id '{stage.Id}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(blueprint.InitialStageId)
            && !seen.Contains(blueprint.InitialStageId))
        {
            errors.Add($"InitialStageId '{blueprint.InitialStageId}' is not declared in stages.");
        }
    }

    private EdgePredicate BuildPredicateForTransition(TransitionBlueprint transition)
    {
        if (transition.Requires.Count == 0)
        {
            return BuiltInPredicates.Always();
        }

        var predicates = new EdgePredicate[transition.Requires.Count];
        for (var i = 0; i < transition.Requires.Count; i++)
        {
            predicates[i] = BuildPredicate(transition.Requires[i]);
        }
        return predicates.Length == 1 ? predicates[0] : BuiltInPredicates.All(predicates);
    }

    private EdgePredicate BuildPredicate(PredicateRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Kind switch
        {
            PredicateKind.AuthLevel => reference.AuthLevel is { } level
                ? BuiltInPredicates.AuthVerificationLevel(level, reference.FailureMessage)
                : throw new ArgumentException("AuthLevel predicate requires AuthLevel to be set."),

            PredicateKind.StateHas => !string.IsNullOrEmpty(reference.Key)
                ? BuiltInPredicates.StateHas(reference.Key, reference.FailureMessage)
                : throw new ArgumentException("StateHas predicate requires Key to be set."),

            PredicateKind.StateEquals => !string.IsNullOrEmpty(reference.Key)
                ? BuiltInPredicates.StateEquals(reference.Key, reference.ExpectedValue, reference.FailureMessage)
                : throw new ArgumentException("StateEquals predicate requires Key to be set."),

            PredicateKind.Named when !string.IsNullOrEmpty(reference.NamedId) =>
                _namedPredicates?.TryResolve(reference.NamedId)
                    ?? throw new InvalidOperationException(
                        _namedPredicates is null
                            ? $"Named predicate '{reference.NamedId}' requested but no INamedEdgePredicateProvider was supplied to the compiler."
                            : $"Named predicate '{reference.NamedId}' is not registered."),

            PredicateKind.Named => throw new ArgumentException("Named predicate requires NamedId to be set."),

            _ => throw new ArgumentOutOfRangeException(nameof(reference.Kind), reference.Kind, "Unknown predicate kind."),
        };
    }
}
