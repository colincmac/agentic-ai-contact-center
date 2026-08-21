using Agents.AI.ContactCenter.AITools;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.Sandbox;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.ToolApproval;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Agents.AI.ContactCenter.DependencyInjection;

// SKETCH — DI wire-up for the new Calling/Proposed shape. Replaces the
// AddConversationHub + ConversationHubBuilder + IContactCenterConversationSessionActivator
// trio with a single AddCallSessionContainer + tier-specific extensions.
//
// Usage:
//
//   builder.AddCallSessionContainer()
//       .AddRealtimeVoiceStrategy()      // Tier 0 — wraps AuthorizingRealtimeAIAgent
//       .AddDtmfStreamingStrategy()      // Tier 4 (streaming edge) — requires a registered ISpeechSynthesizer
//       .AddDashboardProjectionObserver();
//
// Followed by:
//   app.MapCallAutomation();            // updated CallingApi that uses ICallSessionFactory

public static class CallSessionContainerExtensions
{
    /// <summary>
    /// Registers the singleton call container: registry, factory, in-memory quality
    /// reporter, plus the realtime strategy adapter wiring as a transient that
    /// resolves the agent stack at session-create time.
    /// </summary>
    public static CallSessionContainerBuilder AddContactCenter(this IHostApplicationBuilder builder, string communicationOptionsSectionName = CommunicationOptions.SectionName)
    {
        return builder.AddContactCenter(builder.Configuration.GetSection(communicationOptionsSectionName));
    }

    public static CallSessionContainerBuilder AddContactCenter(this IHostApplicationBuilder builder, IConfigurationSection communicationOptionsSection)
    {

        builder.Services.Configure<CommunicationOptions>(communicationOptionsSection);

        return builder.AddCallSessionContainerCore();
    }

    public static CallSessionContainerBuilder AddContactCenter(this IHostApplicationBuilder builder, CommunicationOptions communicationOptions)
    {

        builder.Services.Configure<CommunicationOptions>(options =>
        {
            options = communicationOptions;
        });

        builder.Services.Configure<CallStateOptions>(options =>
        {
            options = communicationOptions.CallState;
        });
        return builder.AddCallSessionContainerCore();
    }


    /// <summary>
    /// Registers the dedicated <see cref="CallingTelemetry"/> singleton for the
    /// new Calling/Proposed stack and wires its <see cref="System.Diagnostics.ActivitySource"/>
    /// / <see cref="System.Diagnostics.Metrics.Meter"/> into the host's
    /// OpenTelemetry pipeline. Invoked automatically by
    /// <see cref="AddCallSessionContainerCore"/>.
    /// </summary>
    private static IHostApplicationBuilder AddCallSessionContainerTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<CallingTelemetry>();

        builder.Services.ConfigureOpenTelemetryTracerProvider((sp, builder) =>
            builder.AddSource(CallingActivitySource.ActivitySourceName));

        builder.Services.ConfigureOpenTelemetryMeterProvider((sp, builder) =>
            builder.AddMeter(CallingActivitySource.MeterName));

        return builder;
    }

    private static CallSessionContainerBuilder AddCallSessionContainerCore(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        builder.AddClusterIdentity();

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CommunicationOptions>>();
            return new CallAutomationClient(options.Value.Acs.ConnectionString);
        });

        services.TryAddSingleton<CallSessionRegistry>();
        services.TryAddSingleton<ICallSessionRegistry>(sp => sp.GetRequiredService<CallSessionRegistry>());

        services.TryAddSingleton<InMemoryCallQualityReporter>();
        services.TryAddSingleton<ICallQualityReporter>(sp => sp.GetRequiredService<InMemoryCallQualityReporter>());

        services.TryAddScoped<CallSessionAccessor>();
        services.TryAddScoped<ICallSessionAccessor>(sp => sp.GetRequiredService<CallSessionAccessor>());

        // Per-call workflow selection, bound by CallSessionFactory before the strategy is built.
        services.TryAddScoped<CallWorkflowSelection>();
        services.TryAddScoped<ICallContextAccessor, CallContextAccessor>();

        // Per-call workflow state and caller-auth state, shared across composite tier swaps
        // because every inner strategy in the same call scope resolves the same instance.
        // The previous design threaded state via a `restoreFrom` ctor parameter on the (now
        // deleted) IConversationStrategyFactory; scoped registration is the equivalent that
        // works naturally with keyed DI.

        services.TryAddScoped(sp =>
        {
            var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();
            var selection = sp.GetRequiredService<CallWorkflowSelection>();
            var compiled = selection.Resolve(catalog);
            return compiled;
        });
        services.TryAddScoped<CallWorkflowSession>();

        // Always-on per-call state plane (projector + folded slices, in-memory store by default).
        // Replaces the previously scoped IvrWorkflowState / CallerAuthenticationState. AddCallState(...)
        // can override the backing store (Redis/Cosmos) and register additional providers.
        services.AddDefaultCallStatePlane();

        // Default Tool Approval service
        services.TryAddScoped<IToolApprovalHandler, RequiresCallerVerificationHandler>();
        services.TryAddScoped<IToolApprovalHandlerProvider, ToolApprovalHandlerProvider>();

        services.TryAddSingleton<ICallSessionFactory, CallSessionFactory>();

        builder.AddCallSessionContainerTelemetry();


        return new CallSessionContainerBuilder(builder);

    }
}

public sealed class CallSessionContainerBuilder(IHostApplicationBuilder builder)
{

    public IHostApplicationBuilder HostApplicationBuilder { get; } = builder;

    public IServiceCollection Services => HostApplicationBuilder.Services;



    /// <summary>
    /// Registers a transfer escalation target so strategies that emit
    /// <see cref="OutboundDirective.TransferCall"/> have a default destination
    /// (e.g. NLU's <c>transfer_to_agent</c> intent, or DTMF "press 0 for agent").
    /// </summary>
    public CallSessionContainerBuilder AddTransferEscalationTarget(string targetIdentifier, TransferKind kind = TransferKind.BlindToPhoneNumber)
    {
        Services.AddSingleton(new TransferEscalationTarget(targetIdentifier, kind));
        return this;
    }

    /// <summary>
    /// Registers per-call sandboxed tool execution on the Azure Container Apps Sandboxes
    /// (ADC) data plane: binds <see cref="CallSandboxOptions"/> from the
    /// <see cref="CallSandboxOptions.SectionName"/> configuration section, registers the
    /// named <see cref="HttpClient"/>, and adds the scoped <see cref="ICallSandboxManager"/>
    /// that owns one microVM per call. The feature stays disabled until
    /// <see cref="CallSandboxOptions.Enabled"/> is set and the sandbox-group coordinates are
    /// supplied (see ADR-0013).
    /// </summary>
    public CallSessionContainerBuilder AddCallSandbox(Action<CallSandboxOptions>? configure = null)
    {

        Services.AddHttpClient(CallSandboxManager.HttpClientName);
        Services.Configure<CallSandboxOptions>(HostApplicationBuilder.Configuration.GetSection(CallSandboxOptions.SectionName));
        if (configure is not null)
        {
            Services.Configure(configure);
        }
        Services.TryAddSingleton<SandboxTelemetry>();
        Services.TryAddScoped<ICallSandboxManager>(sp =>
        {
            var call = sp.GetRequiredService<ICallContextAccessor>().Current
                ?? throw new InvalidOperationException("No call context bound to this scope.");
            return ActivatorUtilities.CreateInstance<CallSandboxManager>(sp, call.CallId);
        });
        return this;
    }

    /// <summary>
    /// Registers the <see cref="CodeInterpreterTools"/> collection (YAML tool name
    /// <see cref="CodeInterpreterTools.ExecutePythonToolName"/>) so workflow stages can run
    /// model-generated Python inside the per-call sandbox. Ensures <see cref="AddCallSandbox"/>
    /// wiring is present.
    /// </summary>
    public CallSessionContainerBuilder AddCodeInterpreterTool()
    {
        AddCallSandbox();
        Services.AddScoped<IAIToolCollection, CodeInterpreterTools>();
        return this;
    }

    /// <summary>
    /// Registers a <see cref="CompositeFallbackStrategy"/> as the keyed <see cref="IConversationStrategy"/>
    /// for <paramref name="topTier"/>. The composite shadows any single-tier registration at the
    /// same key (last-registered wins in MEDI) and walks the <paramref name="orderedTiers"/> chain
    /// on each inner strategy fault, resolving each inner strategy from the per-call scope via
    /// <c>GetRequiredKeyedService&lt;IConversationStrategy&gt;(tier)</c>. Per-call
    /// <see cref="IvrWorkflowState"/> survives swaps because it's registered as scoped in the
    /// call scope and every inner strategy reads the same instance.
    /// </summary>
    /// <param name="topTier">
    /// The tier <see cref="CallSessionFactory"/> will resolve. Must be the first entry of
    /// <paramref name="orderedTiers"/>.
    /// </param>
    /// <param name="orderedTiers">
    /// Ordered fallback chain — first tier is the primary; subsequent tiers are tried in order
    /// when the active inner faults. Tiers without a matching keyed registration are skipped
    /// (with a warning) so the chain remains usable as the host adds tiers incrementally.
    /// </param>
    /// <remarks>
    /// Register the inner strategies (e.g. <see cref="CallWorkflowStrategyExtensions.AddRealtimeCallWorkflowStrategy"/>,
    /// <see cref="CallWorkflowStrategyExtensions.AddNluCallWorkflowStrategy"/>,
    /// <see cref="CallWorkflowStrategyExtensions.AddDtmfCallWorkflowStrategy"/>) BEFORE calling
    /// this method so the composite can resolve them at call-create time.
    /// </remarks>
    public CallSessionContainerBuilder AddCompositeFallbackStrategy(AgentTier topTier, params AgentTier[] orderedTiers)
    {
        if (orderedTiers is null || orderedTiers.Length == 0)
        {
            throw new ArgumentException("Provide at least one tier in the fallback chain.", nameof(orderedTiers));
        }
        if (orderedTiers[0] != topTier)
        {
            throw new ArgumentException(
                $"The first ordered tier ({orderedTiers[0]}) must match topTier ({topTier}).",
                nameof(orderedTiers));
        }

        // Snapshot to defend against caller mutation of the params array after registration.
        var snapshot = orderedTiers.ToArray();

        Services.AddKeyedTransient<IConversationStrategy>(topTier, (sp, _) => new CompositeFallbackStrategy(snapshot,sp.GetRequiredService<CallWorkflowSession>(), sp.GetService<ILoggerFactory>()));
        return this;
    }

    /// <summary>
    /// Wires the hybrid sticky-WS + stateless-webhook ownership router per
    /// ADR-0011 onto the call container. Registers an
    /// <see cref="ICallOwnershipDirectory"/>, an <see cref="IWebhookForwarder"/>,
    /// an <see cref="IPodHeartbeat"/>, and an <see cref="IWebhookIdempotencyStore"/>
    /// so the call-edge can claim ownership on answer, look up the owner on
    /// every mid-call callback, forward to the WS-owning pod when remote, and
    /// release the lease on <c>CallDisconnected</c>.
    /// </summary>
    /// <param name="useRedis">
    /// When <c>true</c> (the default in-cluster posture) wires the Redis-backed
    /// flavour of every primitive plus the <c>HttpWebhookForwarder</c>. The
    /// caller must register an <see cref="StackExchange.Redis.IConnectionMultiplexer"/>
    /// (typically via Aspire's <c>AddRedisClient</c>) and set the
    /// <c>WebhookForwarder</c> section of <see cref="HyperscaleOptions"/>.
    /// When <c>false</c>, wires the in-memory flavour suitable for the showcase
    /// / single-pod dev loop; the <see cref="NullWebhookForwarder"/> resolves
    /// every owner as either local (no-op) or unreachable (drop to the local
    /// fallback path).
    /// </param>
    /// <remarks>
    /// Idempotent — every inner registration uses <c>TryAddSingleton</c>, so
    /// callers may safely co-register the Redis and in-memory flavours of
    /// individual primitives ahead of this call (e.g. Redis ownership +
    /// in-memory idempotency for the pilot tier).
    /// </remarks>
    public CallSessionContainerBuilder AddCallOwnershipRouting(bool useRedis = true)
    {
        if (useRedis)
        {
            HostApplicationBuilder.AddRedisCallOwnershipDirectory();
            HostApplicationBuilder.AddRedisWebhookIdempotencyStore();
            HostApplicationBuilder.AddRedisPodHeartbeat();
            HostApplicationBuilder.AddHttpWebhookForwarder();
        }
        else
        {
            HostApplicationBuilder.AddInMemoryCallOwnershipDirectory();
            HostApplicationBuilder.AddInMemoryWebhookIdempotencyStore();
            HostApplicationBuilder.AddInMemoryPodHeartbeat();
            HostApplicationBuilder.AddInMemoryWebhookForwarder();
        }

        return this;
    }

    /// <summary>
    /// Register the call-control verbs (<c>hang_up_call</c> and <c>transfer_call</c>)
    /// from <see cref="CallControlTools"/> on the <see cref="IvrWorkflow.Tools.IIvrToolRegistry"/>
    /// keyed by <paramref name="agentKey"/>. Both tools require a per-call DI scope (they
    /// reach the live <c>ICallSession</c> via <see cref="ICallSessionAccessor"/>), so they
    /// are registered with <see cref="ServiceLifetime.Scoped"/>.
    /// </summary>
    /// <param name="agentKey">DI service key shared with the realtime agent registration.</param>
    public CallSessionContainerBuilder AddCallControlTools()
    {

        Services.AddScoped<IAIToolCollection, CallControlTools>();

        return this;
    }
}
