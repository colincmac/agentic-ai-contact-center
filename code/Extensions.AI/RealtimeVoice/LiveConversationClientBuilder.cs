using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.RealtimeVoice;

public sealed class LiveConversationClientBuilder
{
    private readonly Func<IServiceProvider, ILiveConversationClient> _innerClientFactory;

    /// <summary>The registered client factory instances.</summary>
    private List<Func<ILiveConversationClient, IServiceProvider, ILiveConversationClient>>? _clientFactories;

    /// <summary>Initializes a new instance of the <see cref="LiveConversationClientBuilder"/> class.</summary>
    /// <param name="innerClient">The inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerClient"/> is <see langword="null"/>.</exception>
    public LiveConversationClientBuilder(ILiveConversationClient innerClient)
    {
        _ = Throw.IfNull(innerClient);
        _innerClientFactory = _ => innerClient;
    }

    /// <summary>Initializes a new instance of the <see cref="LiveConversationClientBuilder"/> class.</summary>
    /// <param name="innerClientFactory">A callback that produces the inner <see cref="ILiveConversationClient"/> that represents the underlying backend.</param>
    public LiveConversationClientBuilder(Func<IServiceProvider, ILiveConversationClient> innerClientFactory)
    {
        _innerClientFactory = Throw.IfNull(innerClientFactory);
    }

    /// <summary>Builds an <see cref="ILiveConversationClient"/> that represents the entire pipeline. Calls to this instance will pass through each of the pipeline stages in turn.</summary>
    /// <param name="services">
    /// The <see cref="IServiceProvider"/> that should provide services to the <see cref="ILiveConversationClient"/> instances.
    /// If <see langword="null"/>, an empty <see cref="IServiceProvider"/> will be used.
    /// </param>
    /// <returns>An instance of <see cref="ILiveConversationClient"/> that represents the entire pipeline.</returns>
    public ILiveConversationClient Build(IServiceProvider? services = null)
    {
        services ??= EmptyServiceProvider.Instance;
        var LiveConversationClient = _innerClientFactory(services);

        // To match intuitive expectations, apply the factories in reverse order, so that the first factory added is the outermost.
        if (_clientFactories is not null)
        {
            for (var i = _clientFactories.Count - 1; i >= 0; i--)
            {
                LiveConversationClient = _clientFactories[i](LiveConversationClient, services);
                if (LiveConversationClient is null)
                {
                    Throw.InvalidOperationException(
                        $"The {nameof(LiveConversationClientBuilder)} entry at index {i} returned null. " +
                        $"Ensure that the callbacks passed to {nameof(Use)} return non-null {nameof(ILiveConversationClient)} instances.");
                }
            }
        }

        return LiveConversationClient;
    }

    /// <summary>Adds a factory for an intermediate chat client to the chat client pipeline.</summary>
    /// <param name="clientFactory">The client factory function.</param>
    /// <returns>The updated <see cref="LiveConversationClientBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clientFactory"/> is <see langword="null"/>.</exception>
    /// <related type="Article" href="https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai#functionality-pipelines">Pipelines of functionality.</related>
    public LiveConversationClientBuilder Use(Func<ILiveConversationClient, ILiveConversationClient> clientFactory)
    {
        _ = Throw.IfNull(clientFactory);

        return Use((innerClient, _) => clientFactory(innerClient));

        
    }

    /// <summary>Adds a factory for an intermediate chat client to the chat client pipeline.</summary>
    /// <param name="clientFactory">The client factory function.</param>
    /// <returns>The updated <see cref="LiveConversationClientBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clientFactory"/> is <see langword="null"/>.</exception>
    /// <related type="Article" href="https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai#functionality-pipelines">Pipelines of functionality.</related>
    public LiveConversationClientBuilder Use(Func<ILiveConversationClient, IServiceProvider, ILiveConversationClient> clientFactory)
    {
        _ = Throw.IfNull(clientFactory);

        (_clientFactories ??= []).Add(clientFactory);
        return this;
    }


    internal sealed class EmptyServiceProvider : IKeyedServiceProvider
    {
        /// <summary>Gets a singleton instance of <see cref="EmptyServiceProvider"/>.</summary>
        public static EmptyServiceProvider Instance { get; } = new();

        /// <inheritdoc />
        public object? GetService(Type serviceType) => null;

        /// <inheritdoc />
        public object? GetKeyedService(Type serviceType, object? serviceKey) => null;

        /// <inheritdoc />
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey) ??
            throw new InvalidOperationException($"No service for type '{serviceType}' and key '{serviceKey}' has been registered.");
    }
}
