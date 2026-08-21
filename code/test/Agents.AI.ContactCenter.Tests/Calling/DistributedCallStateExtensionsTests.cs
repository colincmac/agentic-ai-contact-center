using System.Reflection;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Agents.AI.ContactCenter.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Tests.Calling;

/// <summary>
/// Item 10 — <see cref="DistributedCallStateExtensions.WithDistributedCallState"/>
/// aggregate opt-in. Asserts that one call wires every coordination primitive
/// in ADR-0004 + ADR-0011 with the requested backend, and that the resulting
/// <see cref="ICallSessionFactory"/> picks up <see cref="ICallOwnershipDirectory"/>
/// and <see cref="IPodHeartbeat"/> via constructor injection.
/// </summary>
public class DistributedCallStateExtensionsTests
{
    [Fact]
    public void InMemory_backend_registers_in_process_implementations_for_every_primitive()
    {
        using var host = BuildHost(DistributedCallStateBackend.InMemory);

        Assert.IsType<InMemoryIncomingCallAdmissionController>(host.Services.GetRequiredService<IIncomingCallAdmissionController>());
        Assert.IsType<InMemoryWebhookIdempotencyStore>(host.Services.GetRequiredService<IWebhookIdempotencyStore>());
        Assert.IsType<InMemoryCallOwnershipDirectory>(host.Services.GetRequiredService<ICallOwnershipDirectory>());
        Assert.IsType<InMemoryTierCeilingProvider>(host.Services.GetRequiredService<ITierCeilingProvider>());
        Assert.IsType<InMemoryDistributedCapacityTracker>(host.Services.GetRequiredService<IDistributedCapacityTracker>());
        Assert.IsType<PodHeartbeatService>(host.Services.GetRequiredService<IPodHeartbeat>());
        Assert.IsType<InMemoryPodLeaseStore>(host.Services.GetRequiredService<IPodLeaseStore>());
        Assert.IsType<NullWebhookForwarder>(host.Services.GetRequiredService<IWebhookForwarder>());
        Assert.IsType<DistributedAgentTierResolver>(host.Services.GetRequiredService<IAgentTierResolver>());
        Assert.NotNull(host.Services.GetRequiredService<IClusterIdentity>());
    }

    [Fact]
    public void InMemory_backend_is_the_default_when_no_backend_is_specified()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        SeedConfiguration(builder);
        builder.AddContactCenter().WithDistributedCallState();
        using var host = builder.Build();

        Assert.IsType<InMemoryCallOwnershipDirectory>(host.Services.GetRequiredService<ICallOwnershipDirectory>());
        Assert.IsType<InMemoryIncomingCallAdmissionController>(host.Services.GetRequiredService<IIncomingCallAdmissionController>());
        Assert.IsType<NullWebhookForwarder>(host.Services.GetRequiredService<IWebhookForwarder>());
    }

    [Fact]
    public void Redis_backend_registers_redis_implementations_for_every_primitive()
    {
        // ServiceDescriptor inspection rather than resolution so we don't
        // have to stand up a real IConnectionMultiplexer just to assert wiring.
        var builder = Host.CreateEmptyApplicationBuilder(null);
        SeedConfiguration(builder);
        builder.AddContactCenter().WithDistributedCallState(DistributedCallStateBackend.Redis);
        var services = builder.Services;

        AssertImplementationType<IIncomingCallAdmissionController, RedisIncomingCallAdmissionController>(services);
        AssertImplementationType<IWebhookIdempotencyStore, RedisWebhookIdempotencyStore>(services);
        AssertImplementationType<ICallOwnershipDirectory, RedisCallOwnershipDirectory>(services);
        AssertImplementationType<IPodLeaseStore, RedisPodLeaseStore>(services);
        AssertImplementationType<IDistributedCapacityTracker, RedisDistributedCapacityTracker>(services);
        AssertImplementationType<IAgentTierResolver, DistributedAgentTierResolver>(services);

        // PodHeartbeatService registers itself + factory-delegates IPodHeartbeat to it.
        Assert.Contains(services, d => d.ServiceType == typeof(PodHeartbeatService) && d.ImplementationType == typeof(PodHeartbeatService));
        Assert.Contains(services, d => d.ServiceType == typeof(IPodHeartbeat) && d.ImplementationFactory is not null);

        // RedisTierCeilingProvider follows the same singleton + factory-delegate pattern.
        Assert.Contains(services, d => d.ServiceType == typeof(RedisTierCeilingProvider) && d.ImplementationType == typeof(RedisTierCeilingProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(ITierCeilingProvider) && d.ImplementationFactory is not null);

        // HttpWebhookForwarder is registered via AddHttpClient<>; IWebhookForwarder is a factory delegate.
        Assert.Contains(services, d => d.ServiceType == typeof(IWebhookForwarder) && d.ImplementationFactory is not null);
        Assert.Contains(services, d => d.ServiceType == typeof(HttpWebhookForwarder));

        // PodHeartbeatService is also registered as a hosted service so the heartbeat loop runs.
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null);
    }

    [Fact]
    public void CallSessionFactory_resolves_without_claiming_session_level_streaming_ownership()
    {
        using var host = BuildHost(DistributedCallStateBackend.InMemory);

        var factory = host.Services.GetRequiredService<ICallSessionFactory>();
        Assert.IsType<CallSessionFactory>(factory);
        Assert.Null(typeof(CallSessionFactory).GetField("_ownership", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(CallSessionFactory).GetField("_heartbeat", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void AddDistributedCallState_throws_when_builder_is_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((CallSessionContainerBuilder)null!).WithDistributedCallState());
    }

    [Fact]
    public void AddDistributedCallState_throws_for_unknown_backend()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        SeedConfiguration(builder);
        var container = builder.AddContactCenter();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            container.WithDistributedCallState((DistributedCallStateBackend)int.MaxValue));
    }

    [Fact]
    public void AddDistributedCallState_is_idempotent_when_called_twice()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        SeedConfiguration(builder);
        builder.AddContactCenter()
            .WithDistributedCallState(DistributedCallStateBackend.InMemory)
            .WithDistributedCallState(DistributedCallStateBackend.InMemory);
        using var host = builder.Build();

        // TryAddSingleton inside every per-primitive registrar means a second call is a no-op,
        // not a duplicate registration that would shadow the first.
        Assert.Single(host.Services.GetServices<ICallOwnershipDirectory>());
        Assert.Single(host.Services.GetServices<IIncomingCallAdmissionController>());
        Assert.Single(host.Services.GetServices<IWebhookForwarder>());
        Assert.Single(host.Services.GetServices<IDistributedCapacityTracker>());
    }

    private static IHost BuildHost(DistributedCallStateBackend backend)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        SeedConfiguration(builder);
        builder.AddContactCenter().WithDistributedCallState(backend);
        return builder.Build();
    }

    private static void SeedConfiguration(IHostApplicationBuilder builder)
    {
        // CallAutomationClient is wired via factory delegate and is never resolved by these
        // tests, but seeding a dummy connection string keeps options binding inert if any
        // downstream registrar peeks at IConfiguration.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Communication:Acs:ConnectionString"] = "endpoint=https://example.local;accesskey=ZHVtbXk=",
            ["Hyperscale:ClusterIdentity:ClusterId"] = "cluster-test",
            ["Hyperscale:ClusterIdentity:PodId"] = "pod-test",
        });
    }

    private static void AssertImplementationType<TService, TImpl>(IServiceCollection services)
    {
        Assert.Contains(services, d =>
            d.ServiceType == typeof(TService) &&
            d.ImplementationType == typeof(TImpl));
    }
}
