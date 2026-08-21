using Agents.AI.Monitoring.Correlation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the call-correlation observability primitives in <c>Agents.AI.Monitoring</c>.
/// </summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Register the ambient correlation accessor, the default in-memory correlation store, and
    /// the OpenTelemetry span-enrichment processor. Call once from the host (wired by
    /// <c>ServiceDefaults.ConfigureOpenTelemetry</c>). Idempotent.
    /// </summary>
    public static IServiceCollection AddCallCorrelation(this IServiceCollection services)
    {
        services.TryAddSingleton<ICallCorrelationAccessor, CallCorrelationAccessor>();
        services.TryAddSingleton<ICallCorrelationStore, InMemoryCallCorrelationStore>();
        services.TryAddSingleton<CallCorrelationEnrichmentProcessor>();

        services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddProcessor<CallCorrelationEnrichmentProcessor>());

        return services;
    }

    /// <summary>
    /// Replace the correlation store with the Redis-backed implementation for cross-pod,
    /// durable correlation. Requires a registered <c>IConnectionMultiplexer</c>.
    /// </summary>
    public static IServiceCollection AddRedisCallCorrelationStore(this IServiceCollection services)
    {
        services.RemoveAll<ICallCorrelationStore>();
        services.AddSingleton<ICallCorrelationStore, RedisCallCorrelationStore>();
        return services;
    }
}
