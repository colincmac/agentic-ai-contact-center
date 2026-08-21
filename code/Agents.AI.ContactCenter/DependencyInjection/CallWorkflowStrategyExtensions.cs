using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.Calling.Strategies.Nlu;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI extensions for wiring strategies that operate on the
/// <see cref="IvrWorkflow.Blueprint.WorkflowBlueprint"/> / <see cref="IvrWorkflow.Compilation.CompiledCallWorkflow"/>
/// model. Each <c>Add*CallWorkflowStrategy</c> registers an <see cref="IConversationStrategy"/>
/// as keyed transient at the strategy's <see cref="AgentTier"/>, with a delegate that resolves
/// per-call services from the scope and constructs a fresh strategy instance.
/// </summary>
public static class CallWorkflowStrategyExtensions
{
    /// <summary>
    /// Register a <see cref="RealtimeCallWorkflowStrategy"/> at <see cref="AgentTier.RealtimeVoice"/>.
    /// The workflow is selected per call via <c>CallSessionRequest.WorkflowId</c>;
    /// <paramref name="defaultWorkflowId"/> is used when the request omits one, falling back to
    /// the single registered workflow when the catalog is unambiguous. The caller must also
    /// register the realtime backend (typically via <c>builder.AddRealtimeVoiceStrategy(...)</c>),
    /// the workflow blueprint(s) (via <c>services.AddCallWorkflowsFromDirectory(...)</c>
    /// or <c>services.AddCallWorkflow(...)</c>), and any tools the blueprints reference
    /// (via <c>services.AddIvrTool(agentKey, name, factory, lifetime)</c>).
    /// </summary>
    public static CallSessionContainerBuilder AddRealtimeCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? realtimeAgentServiceKey = null,
        string? defaultWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        //builder.Services.TryAddSingleton<IToolApprovalStore, InMemoryToolApprovalStore>();
        builder.Services.TryAddScoped<IToolApprovalHandlerProvider, ToolApprovalHandlerProvider>();
        builder.Services.TryAddScoped<IToolApprovalHandler, RequiresCallerVerificationHandler>();

        builder.Services.TryAddScoped(sp =>
        {
            var agent = !string.IsNullOrEmpty(realtimeAgentServiceKey)
                ? sp.GetRequiredKeyedService<RealtimeAIAgent>(realtimeAgentServiceKey)
                : sp.GetRequiredService<RealtimeAIAgent>();

            return new AuthorizingAIAgent(
                agent,
                serviceProvider: sp);
        });


        builder.Services.AddKeyedTransient<IConversationStrategy, RealtimeCallWorkflowStrategy>(AgentTier.RealtimeVoice);

        return builder;
    }

    /// <summary>
    /// Register an <see cref="NluCallWorkflowStrategy"/> at <see cref="AgentTier.IntentNlu"/>.
    /// The workflow is selected per call via <c>CallSessionRequest.WorkflowId</c>;
    /// <paramref name="defaultWorkflowId"/> is used when the request omits one, falling back to
    /// the single registered workflow when unambiguous. Requires an
    /// <see cref="Agents.IntentAgent.IvrIntentAgent"/> and
    /// <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI.
    /// </summary>
    public static CallSessionContainerBuilder AddNluCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null,
        string? chatClientServiceKey = null,
        Action<IvrIntentAgentOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new IvrIntentAgentOptions();
        configureOptions?.Invoke(options);

        builder.Services
            .AddOptions<IvrIntentAgentOptions>()
            .Configure(o => configureOptions?.Invoke(o))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.HostApplicationBuilder.AddAIAgent(options.Name, (sp, key) =>
        {
            var chatClient = chatClientServiceKey is null
                ? sp.GetRequiredService<IChatClient>()
                : sp.GetRequiredKeyedService<IChatClient>(chatClientServiceKey);

            var recognizer = sp.GetService<ISpeechRecognizer>();
            var resolvedOptions = sp.GetRequiredService<IOptions<IvrIntentAgentOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();

            return new IvrIntentAgent(chatClient, recognizer, resolvedOptions, loggerFactory);
        });

        builder.Services.TryAddSingleton<IvrIntentAgent>(sp => sp.GetRequiredKeyedService<IvrIntentAgent>(options.Name));

        builder.Services.AddKeyedTransient<IConversationStrategy>(AgentTier.IntentNlu, (sp, _) =>
        {
            var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();
            var selection = sp.GetRequiredService<CallWorkflowSelection>();
            var compiled = selection.Resolve(catalog, defaultWorkflowId);

            var intentAgent = sp.GetRequiredService<IvrIntentAgent>();
            var synthesizer = sp.GetRequiredService<ISpeechSynthesizer>();
            var escalation = sp.GetService<TransferEscalationTarget>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            var session = sp.GetRequiredService<CallWorkflowSession>();

            return new NluCallWorkflowStrategy(
                session,
                intentAgent,
                synthesizer,
                escalation,
                loggerFactory);
        });

        return builder;
    }

    /// <summary>
    /// Register a <see cref="DtmfCallWorkflowStrategy"/> at <see cref="AgentTier.DtmfOnly"/>.
    /// The workflow is selected per call via <c>CallSessionRequest.WorkflowId</c>;
    /// <paramref name="defaultWorkflowId"/> is used when the request omits one, falling back to
    /// the single registered workflow when unambiguous. Requires an
    /// <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI for SSML/text playback.
    /// </summary>
    public static CallSessionContainerBuilder AddDtmfCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ICallWorkflowSessionFactory, CallWorkflowSessionFactory>();

        builder.Services.AddKeyedTransient<IConversationStrategy>(AgentTier.DtmfOnly, (sp, _) =>
        {
            var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();
            var selection = sp.GetRequiredService<CallWorkflowSelection>();
            var compiled = selection.Resolve(catalog, defaultWorkflowId);

            var synthesizer = sp.GetService<ISpeechSynthesizer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var sessionFactory = sp.GetService<ICallWorkflowSessionFactory>()
                ?? new CallWorkflowSessionFactory(loggerFactory);

            var session = sessionFactory.Create(compiled, sp);

            return new DtmfCallWorkflowStrategy(
                session,
                synthesizer,
                loggerFactory);
        });

        return builder;
    }
}
