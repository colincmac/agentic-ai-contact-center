using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class StandardContactCenterExtensionsTests
{
    [Fact]
    public void Communication_options_instance_is_available_to_options_and_acs_client()
    {
        var builder = CreateBuilder(Environments.Development);
        var supplied = new CommunicationOptions
        {
            EnableOpenTelemetryInstrumentation = false,
            Acs = new AcsOptions
            {
                ConnectionString = "endpoint=https://instance.example;accesskey=ZHVtbXk=",
                CallBackUri = new Uri("https://instance.example/callback"),
                MediaStreamingUri = new Uri("wss://instance.example/media")
            },
            Teams = new TeamsOptions
            {
                ResourceTenantId = "tenant",
                ResourceObjectId = "resource",
                PhoneNumber = "+15555550123"
            },
            CallState = new CallStateOptions
            {
                Backend = CallStateBackend.Cosmos,
                SnapshotEveryNEvents = 7
            }
        };

        builder.AddContactCenter(supplied);
        using var host = builder.Build();

        var communication = host.Services.GetRequiredService<IOptions<CommunicationOptions>>().Value;
        var callState = host.Services.GetRequiredService<IOptions<CallStateOptions>>().Value;
        Assert.Equal(supplied.Acs.ConnectionString, communication.Acs.ConnectionString);
        Assert.False(communication.EnableOpenTelemetryInstrumentation);
        Assert.Equal(CallStateBackend.Cosmos, callState.Backend);
        Assert.Equal(7, callState.SnapshotEveryNEvents);
        Assert.NotNull(host.Services.GetRequiredService<CallAutomationClient>());
    }

    [Fact]
    public void Automatic_profile_uses_in_memory_services_in_development()
    {
        var builder = CreateBuilder(Environments.Development);
        var contactCenter = builder.AddStandardContactCenter();
        using var host = builder.Build();

        Assert.Equal(StandardContactCenterProfile.LocalDevelopment, contactCenter.Profile);
        Assert.IsType<InMemoryIncomingCallAdmissionController>(
            host.Services.GetRequiredService<IIncomingCallAdmissionController>());
        Assert.IsType<InMemoryCallStateStore>(host.Services.GetRequiredService<ICallStateStore>());
    }

    [Fact]
    public void Automatic_profile_registers_redis_services_in_production()
    {
        var builder = CreateBuilder(Environments.Production);
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => null!);
        var contactCenter = builder.AddStandardContactCenter();

        Assert.Equal(StandardContactCenterProfile.Production, contactCenter.Profile);
        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IIncomingCallAdmissionController)
            && descriptor.ImplementationType == typeof(RedisIncomingCallAdmissionController));
        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(ICallStateStore)
            && descriptor.ImplementationType == typeof(RedisCallStateStore));
    }

    [Fact]
    public void Production_profile_fails_fast_when_redis_is_not_registered()
    {
        var builder = CreateBuilder(Environments.Production);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddStandardContactCenter());

        Assert.Contains("requires Redis", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddAzureRedisClient", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_voice_fallback_owns_keyed_registration_order_and_can_only_be_added_once()
    {
        var builder = CreateBuilder(Environments.Development);
        var contactCenter = builder.AddStandardContactCenter();
        RegisterFallbackProviders(builder, "nlu");

        contactCenter.UseStandardVoiceFallback("triage", "nlu");

        var strategies = builder.Services
            .Where(static descriptor => descriptor.ServiceType == typeof(IConversationStrategy) && descriptor.IsKeyedService)
            .ToArray();
        Assert.Equal(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly, AgentTier.RealtimeVoice],
            strategies.Select(static descriptor => Assert.IsType<AgentTier>(descriptor.ServiceKey)).ToArray());
        Assert.Throws<InvalidOperationException>(() => contactCenter.UseStandardVoiceFallback("triage", "nlu"));
    }

    [Fact]
    public void Standard_voice_fallback_fails_fast_when_required_providers_are_missing()
    {
        var builder = CreateBuilder(Environments.Development);
        var contactCenter = builder.AddStandardContactCenter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            contactCenter.UseStandardVoiceFallback("triage", "nlu"));

        Assert.Contains("IChatClient 'nlu'", exception.Message, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder CreateBuilder(string environmentName)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Communication:Acs:ConnectionString"] = "endpoint=https://example.local;accesskey=ZHVtbXk=",
            ["Hyperscale:ClusterIdentity:ClusterId"] = "cluster-test",
            ["Hyperscale:ClusterIdentity:PodId"] = "pod-test"
        });
        return builder;
    }

    private static void RegisterFallbackProviders(IHostApplicationBuilder builder, string chatClientKey)
    {
        builder.Services.AddKeyedSingleton<IChatClient>(chatClientKey, (_, _) => null!);
        builder.Services.AddSingleton<ISpeechRecognizer>(_ => null!);
        builder.Services.AddSingleton<ISpeechSynthesizer>(_ => null!);
    }
}
