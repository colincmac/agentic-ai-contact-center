using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies;
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
using Microsoft.Agents.AI;
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
    /// <paramref name="defaultWorkflowId"/> configures the shared workflow default when the request omits one, falling back to
    /// the single registered workflow when the catalog is unambiguous. The caller must also
    /// register the realtime backend (typically via <c>builder.AddRealtimeVoiceStrategy(...)</c>),
    /// the workflow blueprint(s) (via <c>services.AddCallWorkflowsFromDirectory(...)</c>
    /// or <c>services.AddCallWorkflow(...)</c>), and any tools the blueprints reference
    /// (via <c>services.AddIvrTool(factory, lifetime)</c> or <c>AddTools&lt;TTools&gt;()</c>).
    /// </summary>
    public static CallSessionContainerBuilder AddRealtimeCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? realtimeAgentServiceKey = null,
        string? defaultWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureLegacyDefaultWorkflow(builder, defaultWorkflowId);

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


        builder.Services.AddKeyedTransient<ILeafConversationStrategyFactory>(
            AgentTier.RealtimeVoice,
            (sp, _) => new LeafConversationStrategyFactory(
                () => ActivatorUtilities.CreateInstance<RealtimeCallWorkflowStrategy>(sp)));
        builder.Services.AddKeyedTransient<IConversationStrategy>(
            AgentTier.RealtimeVoice,
            (sp, _) => sp.GetRequiredKeyedService<ILeafConversationStrategyFactory>(AgentTier.RealtimeVoice).Create());

        return builder;
    }

    /// <summary>
    /// Register an <see cref="NluCallWorkflowStrategy"/> at <see cref="AgentTier.IntentNlu"/>.
    /// The workflow is selected per call via <c>CallSessionRequest.WorkflowId</c>;
    /// <paramref name="defaultWorkflowId"/> configures the shared workflow default when the request omits one, falling back to
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

        ConfigureLegacyDefaultWorkflow(builder, defaultWorkflowId);

        var options = new IvrIntentAgentOptions();
        configureOptions?.Invoke(options);

        builder.Services
            .AddOptions<IvrIntentAgentOptions>()
            .Configure(o => configureOptions?.Invoke(o))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddKeyedScoped<IvrIntentAgent>(options.Name, (sp, key) =>
        {
            var chatClient = chatClientServiceKey is null
                ? sp.GetRequiredService<IChatClient>()
                : sp.GetRequiredKeyedService<IChatClient>(chatClientServiceKey);

            var recognizer = sp.GetService<ISpeechRecognizer>();
            var resolvedOptions = sp.GetRequiredService<IOptions<IvrIntentAgentOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();

            return new IvrIntentAgent(chatClient, recognizer, resolvedOptions, loggerFactory);
        });

        builder.Services.AddKeyedScoped<AIAgent>(options.Name, (sp, key) => sp.GetRequiredKeyedService<IvrIntentAgent>(key));
        builder.Services.TryAddScoped<IvrIntentAgent>(sp => sp.GetRequiredKeyedService<IvrIntentAgent>(options.Name));

        builder.Services.AddKeyedTransient<ILeafConversationStrategyFactory>(
            AgentTier.IntentNlu,
            (sp, _) => new LeafConversationStrategyFactory(() => new NluCallWorkflowStrategy(
                sp.GetRequiredService<CallWorkflowSession>(),
                sp.GetRequiredService<IvrIntentAgent>(),
                sp.GetRequiredService<ISpeechSynthesizer>(),
                sp.GetService<TransferEscalationTarget>(),
                sp.GetService<ILoggerFactory>())));
        builder.Services.AddKeyedTransient<IConversationStrategy>(
            AgentTier.IntentNlu,
            (sp, _) => sp.GetRequiredKeyedService<ILeafConversationStrategyFactory>(AgentTier.IntentNlu).Create());

        return builder;
    }

    /// <summary>
    /// Register a <see cref="DtmfCallWorkflowStrategy"/> at <see cref="AgentTier.DtmfOnly"/>.
    /// The workflow is selected per call via <c>CallSessionRequest.WorkflowId</c>;
    /// <paramref name="defaultWorkflowId"/> configures the shared workflow default when the request omits one, falling back to
    /// the single registered workflow when unambiguous. Requires an
    /// <see cref="Agents.AI.ContactCenter.Media.Audio.ISpeechSynthesizer"/> in DI for SSML/text playback.
    /// </summary>
    public static CallSessionContainerBuilder AddDtmfCallWorkflowStrategy(
        this CallSessionContainerBuilder builder,
        string? defaultWorkflowId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureLegacyDefaultWorkflow(builder, defaultWorkflowId);

        builder.Services.AddKeyedTransient<ILeafConversationStrategyFactory>(
            AgentTier.DtmfOnly,
            (sp, _) => new LeafConversationStrategyFactory(() => new DtmfCallWorkflowStrategy(
                sp.GetRequiredService<CallWorkflowSession>(),
                sp.GetService<ISpeechSynthesizer>(),
                sp.GetService<ILoggerFactory>())));
        builder.Services.AddKeyedTransient<IConversationStrategy>(
            AgentTier.DtmfOnly,
            (sp, _) => sp.GetRequiredKeyedService<ILeafConversationStrategyFactory>(AgentTier.DtmfOnly).Create());

        return builder;
    }

    private static void ConfigureLegacyDefaultWorkflow(
        CallSessionContainerBuilder builder,
        string? defaultWorkflowId)
    {
        if (!string.IsNullOrWhiteSpace(defaultWorkflowId))
        {
            builder.Services.Configure<CallWorkflowOptions>(
                options => options.DefaultWorkflowId = defaultWorkflowId);
        }
    }
}
