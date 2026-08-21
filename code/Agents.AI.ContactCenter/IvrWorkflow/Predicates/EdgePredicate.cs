namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>Result of evaluating an <see cref="EdgePredicate"/>.</summary>
public readonly record struct EdgePredicateResult(bool Passed, string? FailureReason)
{
    public static EdgePredicateResult Allow() => new(true, null);

    public static EdgePredicateResult Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new EdgePredicateResult(false, reason);
    }
}

/// <summary>
/// Boolean predicate evaluated by the workflow runtime when walking edges out of the
/// current stage. Replaces the legacy <c>IIvrStepGuard</c> chain: a predicate decides
/// whether a single transition is currently allowed.
/// </summary>
/// <param name="context">Per-call state available to the predicate.</param>
/// <param name="cancellationToken">Cancellation token from the runtime.</param>
/// <returns>An <see cref="EdgePredicateResult"/> indicating allow/deny + reason.</returns>
public delegate ValueTask<EdgePredicateResult> EdgePredicate(
    WorkflowEdgeContext context,
    CancellationToken cancellationToken);
