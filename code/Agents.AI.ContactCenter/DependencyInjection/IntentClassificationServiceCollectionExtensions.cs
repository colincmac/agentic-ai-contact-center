using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI helpers for registering <see cref="IvrIntentAgent"/> in a host container.
/// </summary>
/// <remarks>
/// The agent now owns the full intent-recognition pipeline (audio preprocessing via
/// <see cref="ISpeechRecognizer"/>, classification via <see cref="IChatClient"/>, and
/// local tool dispatch when the SLM cannot tool-call). Registration resolves a keyed
/// or default <see cref="IChatClient"/> and an optional <see cref="ISpeechRecognizer"/>
/// from DI; the speech recognizer is required only for callers that use the
/// <see cref="IvrIntentAgent.ClassifyAudioStreamAsync(System.Collections.Generic.IAsyncEnumerable{System.ReadOnlyMemory{byte}}, System.Func{IvrIntentClassificationContext}, System.Threading.CancellationToken)"/>
/// surface or supply inline audio on a <see cref="Microsoft.Extensions.AI.ChatMessage"/>.
/// </remarks>
public static class IntentClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IvrIntentAgent"/> in DI as a keyed singleton (when
    /// <paramref name="agentKey"/> is supplied) or as the default
    /// <see cref="IvrIntentAgent"/> registration. The agent resolves a keyed or default
    /// <see cref="IChatClient"/> for classification and an optional
    /// <see cref="ISpeechRecognizer"/> for audio preprocessing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional callback to configure the agent (name, prompts, tools, …).</param>
    /// <param name="chatClientKey">
    /// Optional DI key for the underlying <see cref="IChatClient"/>. When omitted, the
    /// default (non-keyed) <see cref="IChatClient"/> registration is used.
    /// </param>
    /// <param name="agentKey">
    /// Optional DI key. When supplied, the agent is registered as a keyed singleton; when
    /// omitted, it is registered as the default <see cref="IvrIntentAgent"/> service.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIvrIntentAgent(
        this IServiceCollection services,
        Action<IvrIntentAgentOptions>? configureOptions = null,
        object? chatClientKey = null,
        object? agentKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<IvrIntentAgentOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        IvrIntentAgent Factory(IServiceProvider sp)
        {
            var chatClient = chatClientKey is null
                ? sp.GetRequiredService<IChatClient>()
                : sp.GetRequiredKeyedService<IChatClient>(chatClientKey);
            var recognizer = sp.GetService<ISpeechRecognizer>();
            var options = sp.GetService<IOptions<IvrIntentAgentOptions>>()?.Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new IvrIntentAgent(chatClient, recognizer, options, loggerFactory);
        }

        if (agentKey is null)
        {
            services.TryAddSingleton(Factory);
        }
        else
        {
            services.TryAddKeyedSingleton<IvrIntentAgent>(agentKey, (sp, _) => Factory(sp));
        }

        return services;
    }
}
