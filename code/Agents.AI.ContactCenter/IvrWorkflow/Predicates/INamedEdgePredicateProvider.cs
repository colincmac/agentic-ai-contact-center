namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

/// <summary>
/// Resolves named <see cref="EdgePredicate"/> instances from the current DI scope.
/// Used by the workflow compiler when a YAML <c>requires:</c> entry references a
/// custom predicate by id (e.g. <c>predicate.named(isVip)</c>).
/// </summary>
public interface INamedEdgePredicateProvider
{
    /// <summary>Returns the predicate registered under <paramref name="name"/>, or <see langword="null"/>.</summary>
    EdgePredicate? TryResolve(string name);

    /// <summary>Returns the predicate registered under <paramref name="name"/>, or throws <see cref="System.Collections.Generic.KeyNotFoundException"/>.</summary>
    EdgePredicate Resolve(string name);
}
