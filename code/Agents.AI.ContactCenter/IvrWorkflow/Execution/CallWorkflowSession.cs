using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Per-call bundle that wires the new <see cref="CompiledCallWorkflow"/> model to a
/// concrete strategy.
/// </summary>
/// <remarks>
/// One <see cref="CallWorkflowSession"/> per call. The session owns the workflow + a reference to the
/// call's service scope so executors can resolve tools and predicates without threading the provider
/// through every API call. Per-call state lives in the scoped <c>CallStateProjector</c>, not here.
/// </remarks>
public sealed class CallWorkflowSession
{
    public CallWorkflowSession(
        CompiledCallWorkflow workflow,
        IServiceProvider serviceProvider,
        ICallerElevationDispatcher callerElevationDispatcher,
        IEnumerable<ICredentialAuthenticator>? authenticators = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        Workflow = workflow;
        Services = serviceProvider;
        Authenticators = authenticators?.ToList();
        CallerElevationDispatcher = callerElevationDispatcher;
    }
    public ICallerElevationDispatcher CallerElevationDispatcher { get; }
    public List<ICredentialAuthenticator>? Authenticators { get; }
    public IServiceProvider Services { get; }

    /// <summary>Workflow being walked for this call.</summary>
    public CompiledCallWorkflow Workflow { get; }
}

/// <summary>Factory abstraction so the call-session container can build per-call sessions through DI.</summary>
public interface ICallWorkflowSessionFactory
{
    /// <summary>Create a session for <paramref name="workflow"/>. <paramref name="restoreFrom"/> reuses prior state across tier swaps.</summary>
    CallWorkflowSession Create(
        CompiledCallWorkflow workflow,
        IServiceProvider services);
}

/// <summary>Default <see cref="ICallWorkflowSessionFactory"/>. Singleton; sessions are per-call.</summary>
public sealed class CallWorkflowSessionFactory : ICallWorkflowSessionFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    public CallWorkflowSessionFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public CallWorkflowSession Create(
        CompiledCallWorkflow workflow,
        IServiceProvider services)
    {
        var callerElevationDispatcher = services.GetRequiredService<ICallerElevationDispatcher>();

        return new CallWorkflowSession(workflow, services, callerElevationDispatcher);
    }
}
