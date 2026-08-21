using global::Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Tools;

public sealed class IvrToolServiceCollectionExtensionsTests
{
    //private static AIFunction StubFunction(string name) =>
    //    AIFunctionFactory.Create(() => $"hi from {name}", name);

    //[Fact]
    //public void AddIvrTool_RegistersBinding_OnKeyedRegistry()
    //{
    //    var services = new ServiceCollection();
    //    services.AddIvrTool("triage", "pin-validator", _ => StubFunction("pin-validator"));

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.Equal("triage", registry.AgentKey);
    //    Assert.True(registry.TryGetBinding("pin-validator", out var binding));
    //    Assert.Equal("pin-validator", binding.Name);
    //    Assert.Equal(ServiceLifetime.Singleton, binding.Lifetime);
    //}

    //[Fact]
    //public void AddIvrTool_KeysByAgentKey_RegistriesAreIsolated()
    //{
    //    var services = new ServiceCollection();
    //    services.AddIvrTool("triage", "alpha", _ => StubFunction("alpha"));
    //    services.AddIvrTool("billing", "beta", _ => StubFunction("beta"));

    //    using var sp = services.BuildServiceProvider();
    //    var triage = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");
    //    var billing = sp.GetRequiredKeyedService<IIvrToolRegistry>("billing");

    //    Assert.Contains("alpha", triage.Names);
    //    Assert.DoesNotContain("beta", triage.Names);

    //    Assert.Contains("beta", billing.Names);
    //    Assert.DoesNotContain("alpha", billing.Names);
    //}

    //[Fact]
    //public void AddIvrTool_DuplicateName_LastWins()
    //{
    //    var services = new ServiceCollection();
    //    var first = StubFunction("first");
    //    var second = StubFunction("second");

    //    services.AddIvrTool("triage", "alpha", _ => first);
    //    services.AddIvrTool("triage", "alpha", _ => second);

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.True(registry.TryGetBinding("alpha", out var binding));
    //    Assert.Same(second, binding.Factory(sp));
    //}

    //[Fact]
    //public void AddIvrTool_AIFunctionOverload_RegistersAsSingleton()
    //{
    //    var services = new ServiceCollection();
    //    var fn = StubFunction("greet");

    //    services.AddIvrTool("triage", "greet", fn);

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.True(registry.TryGetBinding("greet", out var binding));
    //    Assert.Equal(ServiceLifetime.Singleton, binding.Lifetime);
    //    Assert.Same(fn, binding.Factory(sp));
    //}

    //[Fact]
    //public void AddIvrTool_FactoryReturningNull_ThrowsOnInvocation()
    //{
    //    var services = new ServiceCollection();
    //    services.AddIvrTool("triage", "alpha", _ => null!);

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.True(registry.TryGetBinding("alpha", out var binding));
    //    Assert.Throws<InvalidOperationException>(() => binding.Factory(sp));
    //}

    //[Fact]
    //public void AddIvrToolRegistry_WithoutTools_ResolvesEmptyRegistry()
    //{
    //    var services = new ServiceCollection();
    //    services.AddIvrToolRegistry("triage");

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.Equal("triage", registry.AgentKey);
    //    Assert.Empty(registry.Names);
    //}

    //[Fact]
    //public void AddIvrToolRegistry_IsIdempotent()
    //{
    //    var services = new ServiceCollection();

    //    services.AddIvrToolRegistry("triage");
    //    services.AddIvrToolRegistry("triage");
    //    services.AddIvrTool("triage", "alpha", _ => StubFunction("alpha"));

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");

    //    Assert.Contains("alpha", registry.Names);
    //}

    //[Fact]
    //public void Factory_IsInvoked_EachTime_BindingFactoryIsCalled()
    //{
    //    var services = new ServiceCollection();
    //    var invocations = 0;

    //    services.AddIvrTool(
    //        "triage",
    //        "counted",
    //        _ =>
    //        {
    //            invocations++;
    //            return StubFunction("counted");
    //        },
    //        ServiceLifetime.Scoped);

    //    using var sp = services.BuildServiceProvider();
    //    var registry = sp.GetRequiredKeyedService<IIvrToolRegistry>("triage");
    //    Assert.True(registry.TryGetBinding("counted", out var binding));

    //    binding.Factory(sp);
    //    binding.Factory(sp);
    //    binding.Factory(sp);

    //    // The registry never caches; per-call caching lives on CallWorkflowSession.
    //    Assert.Equal(3, invocations);
    //}
}
