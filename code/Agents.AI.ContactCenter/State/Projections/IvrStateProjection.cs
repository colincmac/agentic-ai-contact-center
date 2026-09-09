using System.Collections.Immutable;
using System.Text;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow;

namespace Agents.AI.ContactCenter.State.Projections;

/// <summary>
/// Immutable projection of IVR workflow progress: the current step pointer, completed steps, the
/// collected slot values, and overall status. Replaces the decision-state portion of the lock-guarded
/// <c>IvrWorkflowState</c>. Bulk, high-frequency data (transcript, audio-emotion history) is
/// deliberately excluded — that belongs in a separate append log or ring buffer, not a folded snapshot.
/// </summary>
public sealed record IvrSnapshot
{
    /// <summary>The empty starting point for every call.</summary>
    public static readonly IvrSnapshot Empty = new();
    public string? WorkflowId { get; init; }
    public int? WorkflowVersion { get; init; }

    /// <summary>Id of the currently executing stage, or <see langword="null"/> before the first step.</summary>
    public string? CurrentStepId { get; init; }

    /// <summary>Steps completed so far, in order of completion.</summary>
    public ImmutableArray<string> CompletedSteps { get; init; } = [];

    /// <summary>Collected slot values keyed by name; values are stringified for stable serialization.</summary>
    public ImmutableDictionary<string, string?> Slots { get; init; } = [];

    /// <summary>Overall workflow status.</summary>
    public IvrWorkflowStatus Status { get; init; } = IvrWorkflowStatus.NotStarted;
}

/// <summary>
/// Folds workflow-navigation and tool events into an <see cref="IvrSnapshot"/>:
/// <see cref="StrategyEvent.WorkflowStepEntered"/> advances the step pointer and records completion,
/// and <see cref="StrategyEvent.WorkflowDataRecorded"/> records explicitly validated slots.
/// </summary>
public sealed class IvrStateProjection : CallStateProjection<IvrSnapshot>
{
    /// <summary>Stable slice id used as the persistence key.</summary>
    public const string Slice = "ivr";

    public IvrStateProjection() : base(Slice, () => IvrSnapshot.Empty) { }

    protected override IvrSnapshot Apply(IvrSnapshot current, StrategyEvent strategyEvent) => strategyEvent switch
    {
        StrategyEvent.WorkflowSelected e => current with { WorkflowId = e.WorkflowId, WorkflowVersion = e.Version },
        StrategyEvent.WorkflowStepEntered e => EnterStep(current, e.StepId),
        // Only explicitly validated data belongs in workflow state, never arbitrary tool arguments.
        StrategyEvent.WorkflowDataRecorded e => RecordData(current, e.Values),
        StrategyEvent.EscalationRequested => current with { Status = IvrWorkflowStatus.TransferRequested },
        StrategyEvent.WorkflowCompleted e => current with { Status = e.Status },
        _ => current,
    };

    private static IvrSnapshot EnterStep(IvrSnapshot current, string stepId)
    {
        var completed = current.CompletedSteps;
        if (current.CurrentStepId is { } previous
            && !string.Equals(previous, stepId, StringComparison.Ordinal)
            && !completed.Contains(previous))
        {
            completed = completed.Add(previous);
        }

        return current with
        {
            CurrentStepId = stepId,
            CompletedSteps = completed,
            Status = IvrWorkflowStatus.Running,
        };
    }

    private static IvrSnapshot RecordData(IvrSnapshot current, IReadOnlyDictionary<string, string?> values)
    {
        if (values.Count == 0)
        {
            return current;
        }

        var builder = current.Slots.ToBuilder();
        foreach (var (key, value) in values)
        {
            builder[key] = value;
        }

        return current with { Slots = builder.ToImmutable() };
    }

    protected override string? Render(IvrSnapshot state)
    {
        if (state.Slots.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Collected information");
        foreach (var (key, value) in state.Slots)
        {
            sb.AppendLine($"- {key}: {value ?? "<null>"}");
        }

        return sb.ToString();
    }
}
