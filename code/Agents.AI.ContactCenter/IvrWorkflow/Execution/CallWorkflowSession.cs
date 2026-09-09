using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.DependencyInjection;

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
    internal SemaphoreSlim TransitionGate { get; } = new(1, 1);
    internal CredentialCapture CredentialCapture { get; } = new();
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
        Authenticators = (authenticators ?? serviceProvider.GetServices<ICallerAuthenticator>().OfType<ICredentialAuthenticator>()).ToList();
        CallerElevationDispatcher = callerElevationDispatcher;
    }
    public ICallerElevationDispatcher CallerElevationDispatcher { get; }
    public List<ICredentialAuthenticator>? Authenticators { get; }
    public IServiceProvider Services { get; }

    /// <summary>Workflow being walked for this call.</summary>
    public CompiledCallWorkflow Workflow { get; }
}
