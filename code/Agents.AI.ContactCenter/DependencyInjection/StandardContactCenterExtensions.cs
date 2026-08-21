using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>Opinionated runtime profiles for the standard contact-center hosting facade.</summary>
public enum StandardContactCenterProfile
{
    /// <summary>Choose local development or production from the host environment.</summary>
    Automatic,

    /// <summary>Single-process development with in-memory coordination and call state.</summary>
    LocalDevelopment,

    /// <summary>Production runtime with Redis-backed coordination and call state.</summary>
    Production
}

/// <summary>
/// Customer-facing facade over the lower-level contact-center registration APIs. Registrations are
/// applied immediately; advanced hosts can use <see cref="Advanced"/> for capabilities not yet
/// represented by this facade.
/// </summary>
public sealed class StandardContactCenterBuilder
{
    private bool _strategyPipelineConfigured;

    internal StandardContactCenterBuilder(
        CallSessionContainerBuilder advanced,
        StandardContactCenterProfile profile)
    {
        Advanced = advanced;
        Profile = profile;
    }

    /// <summary>The resolved profile used by this host.</summary>
    public StandardContactCenterProfile Profile { get; }

    /// <summary>Access the complete low-level builder for advanced customization.</summary>
    public CallSessionContainerBuilder Advanced { get; }

    /// <summary>Load and validate every YAML workflow beneath <paramref name="directoryPath"/>.</summary>
    public StandardContactCenterBuilder AddWorkflowsFromDirectory(string directoryPath)
    {
        Advanced.Services.AddCallWorkflowsFromDirectory(directoryPath);
        return this;
    }

    /// <summary>Register a typed collection of business tools for per-call workflow resolution.</summary>
    public StandardContactCenterBuilder AddTools<TTools>()
        where TTools : class, IAIToolCollection
    {
        Advanced.Services.AddScoped<IAIToolCollection, TTools>();
        return this;
    }

    /// <summary>Configure caller identification and verification using the existing typed builder.</summary>
    public StandardContactCenterBuilder ConfigureCallerAuthentication(
        Action<CallerAuthenticationContainerExtensions.CallSessionAuthenticationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Advanced.WithCallerAuthentication(configure);
        return this;
    }

    /// <summary>Configure the default human-handoff target used by workflows and call-control tools.</summary>
    public StandardContactCenterBuilder ConfigureHandoff(
        string targetIdentifier,
        TransferKind kind = TransferKind.BlindToPhoneNumber)
    {
        Advanced.AddTransferEscalationTarget(targetIdentifier, kind);
        return this;
    }

    /// <summary>Expose guarded hang-up and transfer tools to workflows.</summary>
    public StandardContactCenterBuilder AddCallControlTools()
    {
        Advanced.AddCallControlTools();
        return this;
    }

    /// <summary>Enable per-call sandboxed Python execution. The feature remains disabled until configured.</summary>
    public StandardContactCenterBuilder EnableCodeInterpreter()
    {
        Advanced.AddCodeInterpreterTool();
        return this;
    }

    /// <summary>
    /// Register the standard resilient strategy chain: realtime voice, intent NLU, then DTMF.
    /// The facade owns registration order and composite setup.
    /// </summary>
    public StandardContactCenterBuilder UseStandardVoiceFallback(
        string? realtimeAgentServiceKey = null,
        string? nluChatClientServiceKey = null)
    {
        if (_strategyPipelineConfigured)
        {
            throw new InvalidOperationException("A strategy pipeline has already been configured for this contact center.");
        }

        ValidateChatClient(nluChatClientServiceKey);
        ValidateService<ISpeechRecognizer>(
            "The standard voice fallback requires an ISpeechRecognizer. Register a speech provider before configuring the fallback chain.");
        ValidateService<ISpeechSynthesizer>(
            "The standard voice fallback requires an ISpeechSynthesizer. Register a speech provider before configuring the fallback chain.");

        Advanced
            .AddRealtimeCallWorkflowStrategy(realtimeAgentServiceKey)
            .AddNluCallWorkflowStrategy(chatClientServiceKey: nluChatClientServiceKey)
            .AddDtmfCallWorkflowStrategy()
            .AddCompositeFallbackStrategy(
                AgentTier.RealtimeVoice,
                AgentTier.RealtimeVoice,
                AgentTier.IntentNlu,
                AgentTier.DtmfOnly);

        _strategyPipelineConfigured = true;
        return this;
    }

    private void ValidateChatClient(string? serviceKey)
    {
        var registered = Advanced.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IChatClient)
            && (serviceKey is null
                ? !descriptor.IsKeyedService
                : descriptor.IsKeyedService && Equals(descriptor.ServiceKey, serviceKey)));
        if (!registered)
        {
            var registration = serviceKey is null
                ? "an unkeyed IChatClient"
                : $"the keyed IChatClient '{serviceKey}'";
            throw new InvalidOperationException(
                $"The standard voice fallback requires {registration} for intent classification. " +
                "Register the chat client before configuring the fallback chain.");
        }
    }

    private void ValidateService<TService>(string message)
    {
        if (!Advanced.Services.Any(static descriptor =>
            descriptor.ServiceType == typeof(TService) && !descriptor.IsKeyedService))
        {
            throw new InvalidOperationException(message);
        }
    }
}

public static class StandardContactCenterExtensions
{
    /// <summary>
    /// Add the standard contact-center runtime. Development uses in-memory state; production uses
    /// Redis-backed coordination and call state and therefore requires an <c>IConnectionMultiplexer</c>.
    /// </summary>
    public static StandardContactCenterBuilder AddStandardContactCenter(
        this IHostApplicationBuilder builder,
        StandardContactCenterProfile profile = StandardContactCenterProfile.Automatic)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedProfile = profile switch
        {
            StandardContactCenterProfile.Automatic when builder.Environment.IsDevelopment()
                => StandardContactCenterProfile.LocalDevelopment,
            StandardContactCenterProfile.Automatic => StandardContactCenterProfile.Production,
            StandardContactCenterProfile.LocalDevelopment => StandardContactCenterProfile.LocalDevelopment,
            StandardContactCenterProfile.Production => StandardContactCenterProfile.Production,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown standard contact-center profile.")
        };

        var advanced = builder.AddContactCenter();
        if (resolvedProfile is StandardContactCenterProfile.LocalDevelopment)
        {
            builder.Services.AddCallCorrelation();
            advanced
                .WithDistributedCallState(DistributedCallStateBackend.InMemory)
                .AddCallState(options => options.Backend = CallStateBackend.InMemory);
        }
        else
        {
            if (!builder.Services.Any(static descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
            {
                throw new InvalidOperationException(
                    "The Production contact-center profile requires Redis. Register an IConnectionMultiplexer " +
                    "before AddStandardContactCenter, for example with AddAzureRedisClient(\"redis\").");
            }

            builder.Services.AddCallCorrelation();
            builder.Services.AddRedisCallCorrelationStore();
            advanced
                .WithDistributedCallState(DistributedCallStateBackend.Redis)
                .AddCallState(options => options.Backend = CallStateBackend.Redis);
        }

        return new StandardContactCenterBuilder(advanced, resolvedProfile);
    }
}
