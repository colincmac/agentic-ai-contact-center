using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.Testing;

/// <summary>Point-in-time outcome of a deterministic contact-center scenario.</summary>
public sealed record ScenarioResult(
    string? CurrentStageId,
    bool IsComplete,
    IvrSnapshot Workflow,
    AuthSnapshot Authentication,
    IReadOnlyList<StrategyEvent> Events,
    IReadOnlyList<string> RenderedStages,
    IReadOnlyList<AuthStepRender> AuthenticationPrompts);

public sealed class ScenarioAssertionException(string message) : Exception(message);

/// <summary>Test-framework-neutral assertions over scenario results.</summary>
public static class ScenarioResultAssertions
{
    public static ScenarioResult ShouldHaveReachedStage(this ScenarioResult result, string stageId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        if (!result.Events.OfType<StrategyEvent.WorkflowStepEntered>()
            .Any(step => string.Equals(step.StepId, stageId, StringComparison.Ordinal)))
        {
            throw new ScenarioAssertionException(
                $"Expected stage '{stageId}' to be reached. Reached: {Format(result.RenderedStages)}.");
        }
        return result;
    }

    public static ScenarioResult ShouldBeAtStage(this ScenarioResult result, string stageId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        if (!string.Equals(result.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException(
                $"Expected current stage '{stageId}', but was '{result.CurrentStageId ?? "<none>"}'.");
        }
        return result;
    }

    public static ScenarioResult ShouldHaveWorkflowStatus(
        this ScenarioResult result,
        IvrWorkflowStatus status)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Workflow.Status != status)
        {
            throw new ScenarioAssertionException(
                $"Expected workflow status '{status}', but was '{result.Workflow.Status}'.");
        }
        return result;
    }

    public static ScenarioResult ShouldHaveVerificationLevel(
        this ScenarioResult result,
        CallerVerificationLevel level)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Authentication.Level != level)
        {
            throw new ScenarioAssertionException(
                $"Expected verification level '{level}', but was '{result.Authentication.Level}'.");
        }
        return result;
    }

    public static ScenarioResult ShouldRequestCredential(
        this ScenarioResult result,
        string authenticatorName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatorName);
        if (!result.AuthenticationPrompts
            .SelectMany(static prompt => prompt.Requests)
            .Any(request => string.Equals(
                request.AuthenticatorName,
                authenticatorName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ScenarioAssertionException(
                $"Expected a credential request for '{authenticatorName}'.");
        }
        return result;
    }

    public static ScenarioResult ShouldContainEvent<TEvent>(
        this ScenarioResult result,
        Func<TEvent, bool>? predicate = null)
        where TEvent : StrategyEvent
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Events.OfType<TEvent>().Any(strategyEvent => predicate?.Invoke(strategyEvent) ?? true))
        {
            throw new ScenarioAssertionException(
                $"Expected an event of type '{typeof(TEvent).Name}'.");
        }
        return result;
    }

    private static string Format(IReadOnlyList<string> values)
        => values.Count == 0 ? "<none>" : string.Join(", ", values);
}
