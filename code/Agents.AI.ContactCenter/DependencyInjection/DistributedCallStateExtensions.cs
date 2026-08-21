using Agents.AI.ContactCenter.Coordination;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// Backing store for the hyperscale call-state plane. Selects whether the
/// ownership directory, capacity tracker, tier ceiling provider, pod
/// heartbeat, and webhook forwarder are wired with their in-process or
/// Redis-backed implementations.
/// </summary>
public enum DistributedCallStateBackend
{
    /// <summary>
    /// In-process / single-pod fallback. Suitable for dev, Aspire, and the
    /// per-pod degraded-mode admission path of ADR-0004. The webhook
    /// forwarder is wired to the no-op <c>NullWebhookForwarder</c> since
    /// there are no peer pods to forward to.
    /// </summary>
    InMemory = 0,

    /// <summary>
    /// Redis + HTTP-backed plane (ADR-0004 + ADR-0011). The caller is
    /// responsible for registering an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (typically
    /// via Aspire's <c>AddRedisClient</c>) before the host starts.
    /// </summary>
    Redis = 1,
}

/// <summary>
/// Aggregate opt-in for the ADR-0004 / ADR-0011 hyperscale call-state plane.
/// Chained after <see cref="CallSessionContainerExtensions.AddContactCenter(IHostApplicationBuilder, string)"/>,
/// it registers — in one call — the webhook idempotency store, call ownership
/// directory, distributed capacity tracker, tier ceiling provider, pod
/// heartbeat, webhook forwarder, and distributed agent tier resolver.
/// </summary>
public static class DistributedCallStateExtensions
{
    /// <summary>
    /// Wires every hyperscale coordination primitive needed for cross-pod
    /// call-state coherence, including admission for the external ACS AnswerCall side effect.
    /// With <see cref="DistributedCallStateBackend.InMemory"/>
    /// this composes purely in-process implementations (suitable for dev,
    /// Aspire, and the degraded-mode fallback). With
    /// <see cref="DistributedCallStateBackend.Redis"/> this composes the
    /// Redis-backed implementations and the HTTP webhook forwarder per
    /// ADR-0011.
    /// </summary>
    /// <remarks>
    /// Once registered, <see cref="ICallOwnershipDirectory"/> and
    /// <see cref="IPodHeartbeat"/> are picked up via constructor injection
    /// by <see cref="Core.CallSessionFactory"/> and
    /// <see cref="Core.CallSession"/> automatically — the
    /// optional ctor params resolve from DI when the services are present.
    /// </remarks>
    public static CallSessionContainerBuilder WithDistributedCallState(
        this CallSessionContainerBuilder builder,
        DistributedCallStateBackend backend = DistributedCallStateBackend.InMemory)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var hostBuilder = builder.HostApplicationBuilder;

        switch (backend)
        {
            case DistributedCallStateBackend.InMemory:
                hostBuilder.AddInMemoryIncomingCallAdmission();
                hostBuilder.AddInMemoryWebhookIdempotencyStore();
                hostBuilder.AddInMemoryCallOwnershipDirectory();
                hostBuilder.AddInMemoryTierCeilingProvider();
                hostBuilder.AddInMemoryDistributedCapacityTracker();
                hostBuilder.AddInMemoryPodHeartbeat();
                hostBuilder.AddInMemoryWebhookForwarder();
                break;

            case DistributedCallStateBackend.Redis:
                hostBuilder.AddRedisIncomingCallAdmission();
                hostBuilder.AddRedisWebhookIdempotencyStore();
                hostBuilder.AddRedisCallOwnershipDirectory();
                hostBuilder.AddRedisTierCeilingProvider();
                hostBuilder.AddRedisDistributedCapacityTracker();
                hostBuilder.AddRedisPodHeartbeat();
                hostBuilder.AddHttpWebhookForwarder();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown distributed call state backend.");
        }

        hostBuilder.AddDistributedAgentTierResolver();
        return builder;
    }
}
