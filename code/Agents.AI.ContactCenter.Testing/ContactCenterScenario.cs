using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Testing;

/// <summary>Creates deterministic scenarios that execute the real workflow compiler and executor.</summary>
public static class ContactCenterScenario
{
    public static ContactCenterScenarioBuilder ForWorkflow(WorkflowBlueprint workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return new ContactCenterScenarioBuilder(workflow);
    }

    public static ContactCenterScenarioBuilder FromYaml(string yaml, string? sourceName = null)
        => ForWorkflow(CallWorkflowYamlReader.Read(yaml, sourceName));

    public static ContactCenterScenarioBuilder FromYamlFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromYaml(File.ReadAllText(path), path);
    }
}

/// <summary>Configures authenticators, tools, predicates, and supporting services for a scenario.</summary>
public sealed class ContactCenterScenarioBuilder
{
    private readonly WorkflowBlueprint _workflow;
    private readonly ServiceCollection _services = new();
    private int _built;

    internal ContactCenterScenarioBuilder(WorkflowBlueprint workflow)
    {
        _workflow = workflow;
        _services.AddLogging();
        _services.AddNamedEdgePredicateProvider();
        _services.AddIvrToolRegistry();
        _services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
    }

    public ContactCenterScenarioBuilder AddAuthenticator(ICallerAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        _services.AddSingleton<ICallerAuthenticator>(authenticator);
        return this;
    }

    public ContactCenterScenarioBuilder AddTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _services.AddIvrTool(tool);
        return this;
    }

    public ContactCenterScenarioBuilder AddNamedPredicate(string name, EdgePredicate predicate)
    {
        _services.AddNamedEdgePredicate(name, predicate);
        return this;
    }

    public ContactCenterScenarioBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_services);
        return this;
    }

    public async Task<ContactCenterScenarioSession> BuildAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _built, 1) != 0)
        {
            throw new InvalidOperationException("A contact-center scenario builder can build only one session.");
        }

        var callId = $"scenario-{Guid.NewGuid():N}";
        var projector = new CallStateProjector(
            callId,
            [new AuthStateProjection(), new IvrStateProjection()],
            new InMemoryCallStateStore(),
            new CallStateOptions());
        _services.AddSingleton(projector);
        _services.AddCallWorkflow(_workflow);

        var root = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var scope = root.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            var metadata = services.GetRequiredService<ICallWorkflowCatalog>().Get(_workflow.Id);
            var workflow = services.GetRequiredService<WorkflowRuntimeBinder>().Bind(metadata);
            var authenticators = services.GetServices<ICallerAuthenticator>().ToArray();
            var workflowSession = new CallWorkflowSession(
                workflow,
                services,
                services.GetRequiredService<ICallerElevationDispatcher>(),
                authenticators.OfType<ICredentialAuthenticator>());
            var recorder = new ScenarioEventRecorder();
            var events = new StateFoldingChannelWriter(recorder, () => projector);
            var renderedStages = new List<string>();
            var authenticationPrompts = new List<AuthStepRender>();
            var executor = new WorkflowExecutor(
                workflowSession,
                events,
                async (stage, token) =>
                {
                    renderedStages.Add(stage.Id);
                    await events.WriteAsync(
                        new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
                        token).ConfigureAwait(false);
                },
                (render, _) =>
                {
                    authenticationPrompts.Add(render);
                    return ValueTask.CompletedTask;
                },
                projectorAccessor: () => projector);

            await projector.HydrateAsync(cancellationToken).ConfigureAwait(false);
            return new ContactCenterScenarioSession(
                root,
                scope,
                projector,
                executor,
                recorder,
                renderedStages,
                authenticationPrompts);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            await root.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
