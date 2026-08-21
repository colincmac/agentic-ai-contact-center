using Extensions.AI.RealtimeVoice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.RealtimeVoice;

/// <summary>
/// Builder extensions to attach the OpenTelemetryConversationSession decorator.
/// </summary>
public static class LiveConversationBuilderExtensions
{
    /// <summary>
    /// Wraps an existing live conversation session with telemetry instrumentation.
    /// </summary>
    /// <param name="session">The original session.</param>
    /// <param name="sourceName">Optional custom source name.</param>
    /// <param name="metadata">Optional chat client metadata (model/provider info).</param>
    /// <param name="configure">Optional callback to further configure the telemetry wrapper.</param>
    public static ILiveConversationClient WithOpenTelemetry(
        this ILiveConversationClient client,
        string? sourceName = null,
        Action<OpenTelemetryConversationClient>? configure = null)
    {
        var wrapper = new OpenTelemetryConversationClient(
            Throw.IfNull(client),
            null,
            sourceName);

        configure?.Invoke(wrapper);
        return wrapper;
    }

    public static LiveConversationClientBuilder UseOpenTelemetry(
        this LiveConversationClientBuilder builder,
        ILoggerFactory? loggerFactory = null,
        string? sourceName = null,
        Action<OpenTelemetryConversationClient>? configure = null) =>
        Throw.IfNull(builder).Use((innerClient, services) =>
    {
        loggerFactory ??= services.GetService<ILoggerFactory>();


        var wrapper = new OpenTelemetryConversationClient(
            Throw.IfNull(innerClient),
            loggerFactory?.CreateLogger<OpenTelemetryConversationClient>(),
            sourceName);

        configure?.Invoke(wrapper);
        return wrapper;
    });

    //public static LiveConversationClientBuilder UseFunctionInvocation(
    //    this LiveConversationClientBuilder builder,
    //    ILoggerFactory? loggerFactory = null,
    //    Action<FunctionInvokingConversationClient>? configure = null)
    //{
    //    _ = Throw.IfNull(builder);

    //    return builder.Use((innerClient, services) =>
    //    {
    //        loggerFactory ??= services.GetService<ILoggerFactory>();

    //        var chatClient = new FunctionInvokingConversationClient(innerClient, loggerFactory, services);
    //        configure?.Invoke(chatClient);
    //        return chatClient;
    //    });
    //}

    public static LiveConversationClientBuilder ConfigureOptions(
        this LiveConversationClientBuilder builder,
        Action<LiveConversationSessionOptions>? configureSessionOptions = null,
        Action<LiveConversationResponseOptions?>? configureResponseOptions = null
    )
    {
        _ = Throw.IfNull(builder);
        _ = Throw.IfNull(configureSessionOptions);
        Func<ILiveConversationSession, ILiveConversationSession> sessionBuilder = (session) => new ConfigureOptionsConversationSession(session, configureSessionOptions, configureResponseOptions);
        return builder.Use(innerClient => new ConfigurableSessionConversationClient(innerClient, sessionBuilder));
    }
}

