using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// DI helpers for the hyperscale coordination plane. These primitives are
/// shared by both in-memory (dev) and distributed (prod) call-state paths;
/// <see cref="IClusterIdentity"/> in particular is required by call
/// telemetry tags (ADR-0004) and is therefore always registered.
/// </summary>
public static class CoordinationServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="HyperscaleOptions"/> from the
    /// <see cref="HyperscaleOptions.SectionName"/> configuration section and
    /// registers the singleton <see cref="IClusterIdentity"/>. Safe to call
    /// multiple times.
    /// </summary>
    public static IHostApplicationBuilder AddClusterIdentity(this IHostApplicationBuilder builder)
    {
        return builder.AddClusterIdentity(builder.Configuration.GetSection(HyperscaleOptions.SectionName));
    }

    /// <summary>
    /// Binds <see cref="HyperscaleOptions"/> from the supplied configuration
    /// section and registers the singleton <see cref="IClusterIdentity"/>.
    /// </summary>
    public static IHostApplicationBuilder AddClusterIdentity(this IHostApplicationBuilder builder, IConfigurationSection hyperscaleSection)
    {
        builder.Services.Configure<HyperscaleOptions>(hyperscaleSection);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IClusterIdentity, HostClusterIdentity>();
        return builder;
    }

    public static IHostApplicationBuilder AddWebhookIdempotencyStore<TWebhookIdempotencyStore>(this IHostApplicationBuilder builder)
    where TWebhookIdempotencyStore : class, IWebhookIdempotencyStore
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookIdempotencyStore, TWebhookIdempotencyStore>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="IWebhookIdempotencyStore"/>
    /// (<see cref="InMemoryWebhookIdempotencyStore"/>). Suitable for dev /
    /// Aspire and for the per-pod fallback in ADR-0004's degraded-mode
    /// admission contract.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryWebhookIdempotencyStore(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookIdempotencyStore, InMemoryWebhookIdempotencyStore>();
        return builder;
    }



    /// <summary>
    /// Registers the Redis-backed <see cref="IWebhookIdempotencyStore"/>
    /// (<see cref="RedisWebhookIdempotencyStore"/>). The caller is responsible
    /// for registering an <see cref="StackExchange.Redis.IConnectionMultiplexer"/>
    /// (typically via Aspire's <c>AddRedisClient</c>).
    /// </summary>
    public static IHostApplicationBuilder AddRedisWebhookIdempotencyStore(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookIdempotencyStore, RedisWebhookIdempotencyStore>();
        return builder;
    }

    public static IHostApplicationBuilder AddInMemoryIncomingCallAdmission(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IIncomingCallAdmissionController, InMemoryIncomingCallAdmissionController>();
        return builder;
    }

    public static IHostApplicationBuilder AddRedisIncomingCallAdmission(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IIncomingCallAdmissionController, RedisIncomingCallAdmissionController>();
        return builder;
    }

    public static IHostApplicationBuilder AddCallOwnershipDirectory<TCallOwnershipDirectory>(this IHostApplicationBuilder builder)
        where TCallOwnershipDirectory : class, ICallOwnershipDirectory
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<ICallOwnershipDirectory, TCallOwnershipDirectory>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="ICallOwnershipDirectory"/>
    /// (<see cref="InMemoryCallOwnershipDirectory"/>). Suitable for dev /
    /// Aspire and for the per-pod fallback in ADR-0004's degraded-mode
    /// admission contract; not suitable for cross-pod callback dispatch.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryCallOwnershipDirectory(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<ICallOwnershipDirectory, InMemoryCallOwnershipDirectory>();
        return builder;
    }

    /// <summary>
    /// Registers the Redis-backed <see cref="ICallOwnershipDirectory"/>
    /// (<see cref="RedisCallOwnershipDirectory"/>) per ADR-0011. The caller is
    /// responsible for registering an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (typically via
    /// Aspire's <c>AddRedisClient</c>).
    /// </summary>
    public static IHostApplicationBuilder AddRedisCallOwnershipDirectory(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<ICallOwnershipDirectory, RedisCallOwnershipDirectory>();
        return builder;
    }

    public static IHostApplicationBuilder AddTierCeilingProvider<TTierCeilingProvider>(this IHostApplicationBuilder builder)
        where TTierCeilingProvider : class, ITierCeilingProvider
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<ITierCeilingProvider, TTierCeilingProvider>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="ITierCeilingProvider"/>
    /// (<see cref="InMemoryTierCeilingProvider"/>). Suitable for dev / Aspire
    /// and for the per-pod fallback in ADR-0004's degraded-mode admission
    /// contract; no Pub/Sub fan-out.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryTierCeilingProvider(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<ITierCeilingProvider, InMemoryTierCeilingProvider>();
        return builder;
    }

    /// <summary>
    /// Registers the Redis-backed <see cref="ITierCeilingProvider"/>
    /// (<see cref="RedisTierCeilingProvider"/>) per ADR-0008. The caller is
    /// responsible for registering an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (typically via
    /// Aspire's <c>AddRedisClient</c>). The same singleton is wired as both
    /// <see cref="ITierCeilingProvider"/> and an
    /// <see cref="IHostedService"/> so the Pub/Sub subscription is established
    /// before the first <c>IncomingCall</c>.
    /// </summary>
    public static IHostApplicationBuilder AddRedisTierCeilingProvider(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<RedisTierCeilingProvider>();
        builder.Services.TryAddSingleton<ITierCeilingProvider>(sp => sp.GetRequiredService<RedisTierCeilingProvider>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RedisTierCeilingProvider>());
        return builder;
    }

    public static IHostApplicationBuilder AddDistributedCapacityTracker<TDistributedCapacityTracker>(this IHostApplicationBuilder builder)
        where TDistributedCapacityTracker : class, IDistributedCapacityTracker
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IDistributedCapacityTracker, TDistributedCapacityTracker>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="IDistributedCapacityTracker"/>
    /// (<see cref="InMemoryDistributedCapacityTracker"/>). Suitable for dev /
    /// Aspire and for the per-pod cluster-local fallback in ADR-0004's
    /// degraded-mode admission contract.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryDistributedCapacityTracker(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IDistributedCapacityTracker, InMemoryDistributedCapacityTracker>();
        return builder;
    }

    /// <summary>
    /// Registers the Redis-backed <see cref="IDistributedCapacityTracker"/>
    /// (<see cref="RedisDistributedCapacityTracker"/>) per ADR-0004. The
    /// caller is responsible for registering an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (typically via
    /// Aspire's <c>AddRedisClient</c>).
    /// </summary>
    public static IHostApplicationBuilder AddRedisDistributedCapacityTracker(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IDistributedCapacityTracker, RedisDistributedCapacityTracker>();
        return builder;
    }

    /// <summary>
    /// Registers the distributed <see cref="IAgentTierResolver"/>
    /// (<see cref="DistributedAgentTierResolver"/>) that composes the
    /// active <see cref="ITierCeilingProvider"/> (ADR-0008) and
    /// <see cref="IDistributedCapacityTracker"/> (ADR-0004) into a single
    /// atomic admit decision. Also binds
    /// <see cref="AgentTierOptions"/> from
    /// <see cref="AgentTierOptions.SectionName"/>. Callers must also
    /// register an <see cref="ITierCeilingProvider"/> and an
    /// <see cref="IDistributedCapacityTracker"/> — either the in-memory or
    /// Redis flavour.
    /// </summary>
    public static IHostApplicationBuilder AddDistributedAgentTierResolver(this IHostApplicationBuilder builder, string sectionName = AgentTierOptions.SectionName)
    {
        builder.Services.Configure<AgentTierOptions>(builder.Configuration.GetSection(sectionName));
        builder.Services.TryAddSingleton<IAgentTierResolver, DistributedAgentTierResolver>();
        return builder;
    }

    /// <summary>
    /// Registers the in-process <see cref="IPodHeartbeat"/>
    /// (<see cref="PodHeartbeatService"/>) backed by an
    /// <see cref="InMemoryPodLeaseStore"/>. Suitable for dev / Aspire; the
    /// cross-pod reaper is degenerate (only the local pod exists) but the
    /// owned-call lease renewal loop still keeps in-memory
    /// <see cref="ICallOwnershipDirectory"/> entries fresh. Requires an
    /// <see cref="ICallOwnershipDirectory"/> to already be registered.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryPodHeartbeat(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IPodLeaseStore, InMemoryPodLeaseStore>();
        builder.Services.TryAddSingleton<PodHeartbeatService>();
        builder.Services.TryAddSingleton<IPodHeartbeat>(sp => sp.GetRequiredService<PodHeartbeatService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PodHeartbeatService>());
        return builder;
    }

    /// <summary>
    /// Registers the Redis-backed <see cref="IPodHeartbeat"/>
    /// (<see cref="PodHeartbeatService"/>) and <see cref="IPodLeaseStore"/>
    /// (<see cref="RedisPodLeaseStore"/>) per ADR-0011. The caller is
    /// responsible for registering an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (typically
    /// via Aspire's <c>AddRedisClient</c>) and an
    /// <see cref="ICallOwnershipDirectory"/> (the Redis flavour for the
    /// reaper sweep to do anything meaningful). The same singleton is wired
    /// as <see cref="IPodHeartbeat"/> and as an <see cref="IHostedService"/>
    /// so the heartbeat loop starts before the first <c>IncomingCall</c>.
    /// </summary>
    public static IHostApplicationBuilder AddRedisPodHeartbeat(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IPodLeaseStore, RedisPodLeaseStore>();
        builder.Services.TryAddSingleton<PodHeartbeatService>();
        builder.Services.TryAddSingleton<IPodHeartbeat>(sp => sp.GetRequiredService<PodHeartbeatService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PodHeartbeatService>());
        return builder;
    }

    /// <summary>
    /// Registers the no-op <see cref="IWebhookForwarder"/>
    /// (<see cref="NullWebhookForwarder"/>). Suitable for dev / Aspire
    /// where every callback is processed by the single local pod; the
    /// returned <see cref="WebhookForwardResult"/> tells callers to handle
    /// the request locally (<see cref="WebhookForwardOutcome.LocalOwner"/>)
    /// or drop to the reaper path
    /// (<see cref="WebhookForwardOutcome.OwnerUnreachable"/>) without
    /// attempting any HTTP transport.
    /// </summary>
    public static IHostApplicationBuilder AddInMemoryWebhookForwarder(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.TryAddSingleton<IWebhookForwarder, NullWebhookForwarder>();
        return builder;
    }

    /// <summary>
    /// Registers the HTTP-based <see cref="IWebhookForwarder"/>
    /// (<see cref="HttpWebhookForwarder"/>) per ADR-0011. The forwarder
    /// targets peer pods via the headless-service DNS template configured
    /// in <see cref="WebhookForwarderOptions"/>. A dedicated typed
    /// <see cref="HttpClient"/> is registered for the forwarder so its
    /// timeout / retry policy is isolated from the application's other
    /// HTTP clients.
    /// </summary>
    public static IHostApplicationBuilder AddHttpWebhookForwarder(this IHostApplicationBuilder builder)
    {
        builder.AddClusterIdentity();
        builder.Services.AddHttpClient<HttpWebhookForwarder>();
        builder.Services.TryAddSingleton<IWebhookForwarder>(sp => sp.GetRequiredService<HttpWebhookForwarder>());
        return builder;
    }
}
