using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI extensions that wire the projection-based call-state plane onto the
/// <see cref="CallSessionContainerBuilder"/> pipeline: the snapshot store (and optional event log) for
/// the selected backend, the per-call <see cref="CallStateProjector"/>, and the registered state
/// projections. Folds the <see cref="StrategyEvent"/> stream into immutable slices on the call's single
/// event-pump thread, replacing the lock-guarded state classes.
/// </summary>
public static class CallStateContainerExtensions
{
    /// <summary>
    /// Registers the call-state store/log for the configured <see cref="CallStateBackend"/>, the scoped
    /// <see cref="CallStateProjector"/>, and the built-in <see cref="AuthStateProjection"/> and
    /// <see cref="IvrStateProjection"/>. Add more slices with <see cref="AddCallStateProjection{TProjection}"/>.
    /// </summary>
    /// <remarks>
    /// Backend prerequisites: <see cref="CallStateBackend.Redis"/> needs an
    /// <c>IConnectionMultiplexer</c> (e.g. Aspire's <c>AddRedisClient</c>);
    /// <see cref="CallStateBackend.Cosmos"/> needs a <c>CosmosClient</c> (e.g. Aspire's
    /// <c>AddAzureCosmosClient</c>) with the configured database/containers provisioned.
    /// </remarks>
    public static CallSessionContainerBuilder AddCallState(
        this CallSessionContainerBuilder builder,
        Action<CallStateOptions>? configure = null)
    {
        var services = builder.Services;

        services.AddOptions<CallStateOptions>().Configure(o => configure?.Invoke(o));

        // The default state plane (projector + AuthStateProjection + IvrStateProjection + in-memory store) is
        // already registered by AddCallSessionContainer. Here we only override the backing store/log for
        // the selected backend.
        services.AddDefaultCallStatePlane();

        // Resolve a concrete options instance now so the backend switch below can read it.
        var options = new CallStateOptions();
        configure?.Invoke(options);

        switch (options.Backend)
        {
            case CallStateBackend.InMemory:
                if (options.EnableEventLog)
                {
                    services.TryAddSingleton<ICallEventLog, InMemoryCallEventLog>();
                }
                break;

            case CallStateBackend.Redis:
                services.RemoveAll<ICallStateStore>();
                services.AddSingleton<ICallStateStore, RedisCallStateStore>();
                if (options.EnableEventLog)
                {
                    services.TryAddSingleton<ICallEventLog, RedisCallEventLog>();
                }
                break;

            case CallStateBackend.Cosmos:
                services.RemoveAll<ICallStateStore>();
                services.AddSingleton<ICallStateStore, CosmosCallStateStore>();
                if (options.EnableEventLog)
                {
                    services.TryAddSingleton<ICallEventLog, CosmosCallEventLog>();
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(configure), options.Backend, "Unknown call-state backend.");
        }

        return builder;
    }

    /// <summary>
    /// Registers the always-on per-call state plane: the scoped <see cref="CallStateProjector"/>, the
    /// built-in <see cref="AuthStateProjection"/> and <see cref="IvrStateProjection"/>, and a default
    /// in-memory <see cref="ICallStateStore"/>. Called by <c>AddCallSessionContainer</c> so the workflow
    /// executor, predicates, authenticators, and tools always have a state plane; <see cref="AddCallState"/>
    /// can override the backing store and add more projections.
    /// </summary>
    internal static IServiceCollection AddDefaultCallStatePlane(this IServiceCollection services)
    {
        services.AddOptions<CallStateOptions>();
        services.TryAddSingleton<ICallStateStore, InMemoryCallStateStore>();

        // One projector per call scope, keyed off the bound session's call id.
        services.TryAddScoped(sp =>
        {
            var accessor = sp.GetRequiredService<ICallContextAccessor>();
            var callId = accessor.Current?.CallId
                ?? throw new InvalidOperationException(
                    "CallStateProjector resolved before a call state was bound to the scope.");

            return new CallStateProjector(
                callId,
                sp.GetServices<ICallStateProjection>(),
                sp.GetRequiredService<ICallStateStore>(),
                sp.GetRequiredService<IOptions<CallStateOptions>>().Value,
                sp.GetService<ICallEventLog>(),
                sp.GetService<ILoggerFactory>());
        });

        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICallStateProjection, AuthStateProjection>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICallStateProjection, IvrStateProjection>());

        return services;
    }

    /// <summary>
    /// Registers an additional <see cref="ICallStateProjection"/> slice. Additive — adding a new state
    /// type is one line and touches no core type. Projections are scoped so each call gets its own
    /// instance composed into that call's <see cref="CallStateProjector"/>.
    /// </summary>
    public static CallSessionContainerBuilder AddCallStateProjection<TProjection>(this CallSessionContainerBuilder builder)
        where TProjection : class, ICallStateProjection
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ICallStateProjection, TProjection>());
        return builder;
    }
}
