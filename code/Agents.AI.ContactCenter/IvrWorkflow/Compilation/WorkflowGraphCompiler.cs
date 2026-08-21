using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Translates a <see cref="WorkflowBlueprint"/> into a runtime <see cref="CompiledCallWorkflow"/>.
/// Validates every <see cref="PredicateRef"/> and retains named predicate references for
/// call-scoped binding, collects every authored tool name,
/// validates graph structure (no duplicate ids, every transition target exists, an initial
/// stage exists), and produces immutable <see cref="CompiledStage"/> nodes with pre-built
/// safe built-in edge predicates and symbolic runtime bindings.
/// </summary>
/// <remarks>
/// The compiler is process-safe and never resolves services. Tool and named-predicate
/// availability is validated separately, then concrete bindings are materialized per call.
/// </remarks>
public sealed class WorkflowGraphCompiler
{
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

                EdgePredicate? predicate;
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

            var toolNames = CollectStageToolNames(blueprint, stage);

            compiledStages[stage.Id] = new CompiledStage(stage, edges, toolNames, basePrompt: blueprint.BasePrompt);
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
    /// Collect every tool name referenced by the workflow and stage, deduped in author order.
    /// </summary>
    private static IReadOnlyList<string> CollectStageToolNames(
        WorkflowBlueprint blueprint,
        StageBlueprint stage)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(blueprint.CommonToolNames, ordered, seen);
        Collect(stage.ToolNames, ordered, seen);
        if (stage.Channels.Realtime is { ToolNames.Count: > 0 } realtime)
        {
            Collect(realtime.ToolNames, ordered, seen);
        }

        return ordered;
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

    internal static EdgePredicate? BuildPredicateForTransition(
        TransitionBlueprint transition,
        INamedEdgePredicateProvider? namedPredicates = null)
    {
        if (transition.Requires.Count == 0)
        {
            return BuiltInPredicates.Always();
        }

        var predicates = new EdgePredicate[transition.Requires.Count];
        for (var i = 0; i < transition.Requires.Count; i++)
        {
            var predicate = BuildPredicate(transition.Requires[i], namedPredicates);
            if (predicate is null)
            {
                return null;
            }
            predicates[i] = predicate;
        }
        return predicates.Length == 1 ? predicates[0] : BuiltInPredicates.All(predicates);
    }

    private static EdgePredicate? BuildPredicate(
        PredicateRef reference,
        INamedEdgePredicateProvider? namedPredicates)
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
                namedPredicates is null
                    ? null
                    : namedPredicates.TryResolve(reference.NamedId)
                        ?? throw new InvalidOperationException($"Named predicate '{reference.NamedId}' is not registered."),

            PredicateKind.Named => throw new ArgumentException("Named predicate requires NamedId to be set."),

            _ => throw new ArgumentOutOfRangeException(nameof(reference.Kind), reference.Kind, "Unknown predicate kind."),
        };
    }
}
