using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>
/// Factory methods for the predicates the workflow compiler emits from YAML
/// <c>requires:</c> entries. Each factory captures its operands and returns a closure
/// matching the <see cref="EdgePredicate"/> delegate.
/// </summary>
public static class BuiltInPredicates
{
    /// <summary>Always allow.</summary>
    public static EdgePredicate Always() =>
        (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());

    /// <summary>Always deny with the supplied reason.</summary>
    public static EdgePredicate Never(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny(reason));
    }

    /// <summary>
    /// Allow only when the caller's folded <see cref="State.Projections.AuthSnapshot.Level"/> is greater
    /// than or equal to <paramref name="minimumLevel"/>. When no state plane is configured the snapshot
    /// is empty (<see cref="CallerVerificationLevel.None"/>), so any non-trivial requirement fails closed.
    /// </summary>
    public static EdgePredicate AuthVerificationLevel(
        CallerVerificationLevel minimumLevel,
        string? failureMessage = null) =>
        (ctx, _) =>
        {
            var current = ctx.Auth.Level;
            return ValueTask.FromResult(current >= minimumLevel
                ? EdgePredicateResult.Allow()
                : EdgePredicateResult.Deny(
                    failureMessage ?? $"Caller verification level '{current}' is below required '{minimumLevel}'."));
        };

    /// <summary>Allow when the folded workflow slot under <paramref name="key"/> is present and non-null.</summary>
    public static EdgePredicate StateHas(string key, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return (ctx, _) => ValueTask.FromResult(
            ctx.Workflow.Slots.TryGetValue(key, out var value) && value is not null
            ? EdgePredicateResult.Allow()
            : EdgePredicateResult.Deny(failureMessage ?? $"Workflow state is missing required key '{key}'."));
    }

    /// <summary>
    /// Allow when the folded slot under <paramref name="key"/> equals <paramref name="expected"/>.
    /// Slots are stored stringified, so <paramref name="expected"/> is compared by its string form.
    /// </summary>
    public static EdgePredicate StateEquals(string key, object? expected, string? failureMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return (ctx, _) =>
        {
            var actual = ctx.Workflow.Slots.TryGetValue(key, out var value) ? value : null;
            var expectedString = expected as string ?? expected?.ToString();
            return ValueTask.FromResult(string.Equals(actual, expectedString, StringComparison.Ordinal)
                ? EdgePredicateResult.Allow()
                : EdgePredicateResult.Deny(
                    failureMessage ?? $"Workflow state '{key}' = '{actual ?? "<null>"}', expected '{expected ?? "<null>"}'."));
        };
    }

    /// <summary>Combine multiple predicates with logical AND. Short-circuits on the first denial.</summary>
    public static EdgePredicate All(params EdgePredicate[] predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        return async (ctx, ct) =>
        {
            for (var i = 0; i < predicates.Length; i++)
            {
                var result = await predicates[i](ctx, ct).ConfigureAwait(false);
                if (!result.Passed)
                {
                    return result;
                }
            }
            return EdgePredicateResult.Allow();
        };
    }

    /// <summary>Combine multiple predicates with logical OR. Short-circuits on the first allowance. Returns the last denial reason when none pass.</summary>
    public static EdgePredicate Any(params EdgePredicate[] predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        if (predicates.Length == 0)
        {
            return Never("Any() called with no predicates; nothing can match.");
        }
        return async (ctx, ct) =>
        {
            EdgePredicateResult last = EdgePredicateResult.Deny("No predicate matched.");
            for (var i = 0; i < predicates.Length; i++)
            {
                last = await predicates[i](ctx, ct).ConfigureAwait(false);
                if (last.Passed)
                {
                    return last;
                }
            }
            return last;
        };
    }

    /// <summary>Negate a predicate. <paramref name="onAllow"/> becomes the denial reason when the inner predicate passes.</summary>
    public static EdgePredicate Not(EdgePredicate predicate, string onAllow = "Negated predicate matched.")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return async (ctx, ct) =>
        {
            var result = await predicate(ctx, ct).ConfigureAwait(false);
            return result.Passed
                ? EdgePredicateResult.Deny(onAllow)
                : EdgePredicateResult.Allow();
        };
    }
}
