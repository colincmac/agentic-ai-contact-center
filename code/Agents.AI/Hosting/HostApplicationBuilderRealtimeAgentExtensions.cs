using System;
using Agents.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Hosting;

/// <summary>
/// Provides extension methods for configuring AI agents in a host application builder.
/// </summary>
public static class HostApplicationBuilderAgentExtensions
{
    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, IRealtimeClient realtimeClient, string? description = null, string? instructions = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(realtimeClient);

        return RegisterRealtimeAIAgent(
            builder,
            name,
            _ => realtimeClient,
            CreateOptions(name, description, instructions));
    }

    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, string? description = null, string? instructions = null, string? realtimeClientKey = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);

        return RegisterRealtimeAIAgent(
            builder,
            name,
            sp => GetRequiredRealtimeClient(sp, realtimeClientKey),
            CreateOptions(name, description, instructions));
    }

    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, IConfigurationSection configurationSection, string? realtimeClientKey = null, Action<RealtimeAgentOptions>? configureOptions = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(configurationSection);

        var options = configurationSection.Get<RealtimeAgentOptions>();
        Throw.IfNull(options);

        configureOptions?.Invoke(options);
        options.Name ??= name;

        return builder.AddRealtimeAIAgent(name, options, realtimeClientKey);
    }

    public static IHostApplicationBuilder AddRealtimeAIAgent(this IHostApplicationBuilder builder, string name, RealtimeAgentOptions options, string? liveConversationClientKey = null)
    {
        Throw.IfNull(builder);
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(options);

        options.Name ??= name;

        return RegisterRealtimeAIAgent(
            builder,
            name,
            sp => GetRequiredRealtimeClient(sp, liveConversationClientKey),
            options);
    }

    private static IHostApplicationBuilder RegisterRealtimeAIAgent(IHostApplicationBuilder builder, string name, Func<IServiceProvider, IRealtimeClient> realtimeClientFactory, RealtimeAgentOptions options)
    {
        builder.AddAIAgent(
            name,
            (sp, _) => new RealtimeAIAgent(
                realtimeClient: realtimeClientFactory(sp),
                options: options,
                sp.GetRequiredService<ILoggerFactory>()));

        builder.Services.AddKeyedSingleton<RealtimeAIAgent>(
            name,
            (sp, _) =>
            {
                var agent = sp.GetRequiredKeyedService<AIAgent>(name);
                return agent is RealtimeAIAgent realtimeAgent
                    ? realtimeAgent
                    : throw new InvalidOperationException($"Could not resolve {nameof(RealtimeAIAgent)} for '{name}'.");
            });

        return builder;
    }

    private static RealtimeAgentOptions CreateOptions(string name, string? description, string? instructions) =>
        new()
        {
            Name = name,
            Description = description,
            SessionOptions = !string.IsNullOrEmpty(instructions) ? new RealtimeSessionOptions
            {
                Instructions = instructions,
            } : null,
        };

    private static IRealtimeClient GetRequiredRealtimeClient(IServiceProvider serviceProvider, string? clientKey) =>
        clientKey is null
            ? serviceProvider.GetRequiredService<IRealtimeClient>()
            : serviceProvider.GetRequiredKeyedService<IRealtimeClient>(clientKey);
}
