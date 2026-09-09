using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authorization;
using Agents.AI.ContactCenter.Authorization;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI extensions for the workflow framework. Replaces the larger surface in
/// <c>IvrWorkflowServiceCollectionExtensions</c> with a minimal set: register the compiler,
/// register one or more <see cref="WorkflowBlueprint"/> instances, build the catalog.
/// </summary>
/// <remarks>
/// Workflows are compiled into process-shared metadata and validated at host startup.
/// Concrete tools and named predicates resolve from the active call scope.
/// </remarks>
public static class CallWorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Register the workflow compiler and a <see cref="ICallWorkflowCatalog"/> that
    /// materializes from every <see cref="WorkflowBlueprint"/> resolvable from the root
    /// scope. Idempotent. Runtime tool and predicate binding remains scoped per call.
    /// </summary>
    public static IServiceCollection AddCallWorkflowFramework(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddNamedEdgePredicateProvider();
        services.AddIvrToolRegistry();
        services.AddOptions<CallWorkflowOptions>();
        services.AddAuthorizationCore();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, WorkflowActionAuthorizationHandler>());
        services.TryAddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        services.TryAddScoped<CallActionDispatcher>();
        services.AddOptions<CallIngressOptions>().ValidateOnStart();
        services.TryAddSingleton<CallIngressRouter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CallIngressOptions>, CallIngressOptionsValidator>());

        services.TryAddSingleton<WorkflowGraphCompiler>();
        services.TryAddScoped<WorkflowRuntimeBinder>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, WorkflowStartupValidationService>());

        services.TryAddSingleton<ICallWorkflowCatalog>(sp =>
        {
            var compiler = sp.GetRequiredService<WorkflowGraphCompiler>();
            var blueprints = sp.GetServices<WorkflowBlueprint>();
            var compiled = blueprints.Select(compiler.Compile);
            return new CallWorkflowCatalog(compiled);
        });

        return services;
    }

    /// <summary>
    /// Register a <see cref="WorkflowBlueprint"/> with the framework. The blueprint is
    /// compiled into the catalog at first resolution. Multiple calls add additional
    /// workflows; their ids must be unique.
    /// </summary>
    public static IServiceCollection AddCallWorkflow(
        this IServiceCollection services,
        WorkflowBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(blueprint);

        services.AddCallWorkflowFramework();
        services.AddSingleton(blueprint);
        return services;
    }

    /// <summary>Factory overload of <see cref="AddCallWorkflow(IServiceCollection, WorkflowBlueprint)"/>.</summary>
    public static IServiceCollection AddCallWorkflow(
        this IServiceCollection services,
        Func<IServiceProvider, WorkflowBlueprint> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddCallWorkflowFramework();
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Register the default <see cref="INamedEdgePredicateProvider"/>. Called automatically
    /// by <see cref="AddNamedEdgePredicate(IServiceCollection, string, Func{IServiceProvider, EdgePredicate}, ServiceLifetime)"/>;
    /// call directly when predicates are added by other means.
    /// </summary>
    public static IServiceCollection AddNamedEdgePredicateProvider(this IServiceCollection services)
    {
        services.TryAddScoped<INamedEdgePredicateProvider, NamedEdgePredicateProvider>();
        return services;
    }

    /// <summary>Register a named <see cref="EdgePredicate"/>. Last-wins on duplicate names.</summary>
    public static IServiceCollection AddNamedEdgePredicate(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, EdgePredicate> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddNamedEdgePredicateProvider();

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(EdgePredicate)
                && descriptor.IsKeyedService
                && descriptor.ServiceKey is string existing
                && string.Equals(existing, name, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }

        services.TryAdd(new ServiceDescriptor(
            typeof(EdgePredicate),
            name,
            (sp, _) => factory(sp),
            lifetime));

        return services;
    }

    /// <summary>Singleton-instance overload of <see cref="AddNamedEdgePredicate(IServiceCollection, string, Func{IServiceProvider, EdgePredicate}, ServiceLifetime)"/>.</summary>
    public static IServiceCollection AddNamedEdgePredicate(
        this IServiceCollection services,
        string name,
        EdgePredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return services.AddNamedEdgePredicate(name, _ => predicate, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Register an <see cref="AITool"/> for call-scoped workflow binding.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Factory invoked per call to materialize the function from the call scope.</param>
    /// <param name="lifetime">
    /// Descriptive hint:
    /// <see cref="ServiceLifetime.Singleton"/> for stateless tools,
    /// <see cref="ServiceLifetime.Scoped"/> for tools that capture per-call state
    /// (e.g. <c>CallerAuthenticationState</c>, <c>ICallSessionAccessor</c>),
    /// <see cref="ServiceLifetime.Transient"/> when each materialization should produce a fresh instance.
    /// The registry never caches results; per-call caching lives on
    /// <see cref="Agents.AI.ContactCenter.IvrWorkflow.Execution.CallWorkflowSession"/>.
    /// </param>
    public static IServiceCollection AddIvrTool(
        this IServiceCollection services,
        Func<IServiceProvider, AITool> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddIvrToolRegistry();

        services.Add(new ServiceDescriptor(typeof(AITool), sp => factory(sp), lifetime));

        return services;
    }

    /// <summary>
    /// Convenience overload that registers an already-built tool.
    /// </summary>
    public static IServiceCollection AddIvrTool(
        this IServiceCollection services,
        AITool tool,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return services.AddIvrTool(_ => tool, lifetime);
    }

    /// <summary>
    /// Register the call-scoped <see cref="IIvrToolRegistry"/>. Invoked automatically by
    /// <see cref="AddIvrTool(IServiceCollection, Func{IServiceProvider, AITool}, ServiceLifetime)"/>.
    /// </summary>
    public static IServiceCollection AddIvrToolRegistry(this IServiceCollection services)
    {

        services.TryAddScoped<IIvrToolRegistry>(sp =>
        {
            var tools = sp.GetServices<AITool>();
            var toolCollections = sp.GetServices<IAIToolCollection>();
            return new IvrToolRegistry([.. tools, .. toolCollections.SelectMany(t => t.AsAITools())]);
        });

        return services;
    }
}
