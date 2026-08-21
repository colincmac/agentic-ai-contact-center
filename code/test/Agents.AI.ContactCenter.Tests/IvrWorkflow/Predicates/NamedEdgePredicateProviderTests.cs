using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Predicates;

public sealed class NamedEdgePredicateProviderTests
{
    [Fact]
    public void Resolve_ReturnsRegisteredPredicate()
    {
        var services = new ServiceCollection();
        EdgePredicate p = (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());
        services.AddNamedEdgePredicate("isVip", p);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<INamedEdgePredicateProvider>();
        Assert.NotNull(provider.TryResolve("isVip"));
        Assert.Same(p, provider.Resolve("isVip"));
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        var services = new ServiceCollection();
        services.AddNamedEdgePredicateProvider();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<INamedEdgePredicateProvider>();
        Assert.Throws<KeyNotFoundException>(() => provider.Resolve("missing"));
    }

    [Fact]
    public void Register_SameName_LastWins()
    {
        EdgePredicate first = (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny("first"));
        EdgePredicate second = (_, _) => ValueTask.FromResult(EdgePredicateResult.Deny("second"));

        var services = new ServiceCollection();
        services.AddNamedEdgePredicate("p", first);
        services.AddNamedEdgePredicate("p", second);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<INamedEdgePredicateProvider>();
        Assert.Same(second, provider.Resolve("p"));
    }
}
