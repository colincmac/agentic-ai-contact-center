using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.IvrWorkflow.Predicates;

internal sealed class NamedEdgePredicateProvider : INamedEdgePredicateProvider
{
    private readonly IServiceProvider _services;

    public NamedEdgePredicateProvider(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public EdgePredicate? TryResolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _services.GetKeyedService<EdgePredicate>(name);
    }

    public EdgePredicate Resolve(string name) =>
        TryResolve(name) ?? throw new KeyNotFoundException(
            $"No EdgePredicate is registered under the name '{name}'. " +
            "Register one with services.AddNamedEdgePredicate(name, ...).");
}
