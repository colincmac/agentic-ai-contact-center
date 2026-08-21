//using System.Text.Json;
//using Agents.AI.Extensions.Helpers.Streaming;
//using Microsoft.Agents.AI;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Shared.Diagnostics;

//namespace Agents.AI.Extensions.AIContextExtensions;

///// <summary>
///// An <see cref="AIContextProvider"/> that composes multiple inner providers, combining their
///// context contributions and forwarding lifecycle notifications.
///// </summary>
///// <remarks>
///// <para>
///// The <see cref="CompositeAIContextProvider"/> allows you to configure multiple independent
///// <see cref="AIContextProvider"/> instances and treat them as a single provider attached to a thread
///// (for example, a combination of <see cref="Data.TextSearchProvider"/> and
///// <see cref="ChatHistoryMemoryProvider"/>).
///// </para>
///// <para>
///// Behavior:
///// <list type="bullet">
///// <item><description>
///// During <see cref="InvokingAsync(InvokingContext, CancellationToken)"/>, all inner providers are invoked
///// in order. Their returned <see cref="AIContext"/> values are merged into a single context:
///// <list type="bullet">
///// <item><description><see cref="AIContext.Instructions"/> are concatenated with newlines.</description></item>
///// <item><description><see cref="AIContext.Messages"/> are concatenated into a single list.</description></item>
///// <item><description><see cref="AIContext.Tools"/> are concatenated into a single list.</description></item>
///// </list>
///// </description></item>
///// <item><description>
///// During <see cref="InvokedAsync(InvokedContext, CancellationToken)"/>, notifications are forwarded
///// to all inner providers. An exception from one provider does not prevent others from being notified; any
///// exceptions are logged but not rethrown.
///// </description></item>
///// <item><description>
///// <see cref="Serialize(JsonSerializerOptions?)"/> returns a composite state that callers can later use
///// to reconstruct the composite with equivalent inner provider state, provided they supply matching
///// factory functions.
///// </description></item>
///// </list>
///// </para>
///// </remarks>
//public sealed class CompositeAIContextProvider : AIContextProvider
//{
//    private readonly IReadOnlyList<AIContextProvider> _providers;
//    private readonly ILogger<CompositeAIContextProvider>? _logger;

//    /// <summary>
//    /// Initializes a new instance of the <see cref="CompositeAIContextProvider"/> class.
//    /// </summary>
//    /// <param name="providers">
//    /// The ordered collection of child <see cref="AIContextProvider"/> instances that will participate
//    /// in the context lifecycle. Must not be <see langword="null"/> or contain <see langword="null"/> entries.
//    /// </param>
//    /// <param name="loggerFactory">Optional logger factory.</param>
//    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
//    public CompositeAIContextProvider(
//        IEnumerable<AIContextProvider> providers,
//        ILoggerFactory? loggerFactory = null)
//    {
//        _ = Throw.IfNull(providers);

//        var list = providers.ToList();
//        if (list.Any(p => p is null))
//        {
//            throw new ArgumentException("Providers collection contains null entries.", nameof(providers));
//        }

//        this._providers = list;
//        this._logger = loggerFactory?.CreateLogger<CompositeAIContextProvider>();
//    }

//    /// <summary>
//    /// Initializes a new instance of the <see cref="CompositeAIContextProvider"/> class from previously serialized state.
//    /// </summary>
//    /// <param name="serializedState">The serialized composite provider state.</param>
//    /// <param name="jsonSerializerOptions">JSON serializer options.</param>
//    /// <param name="childFactory">
//    /// A factory method that, given a serialized child provider record, returns an <see cref="AIContextProvider"/>.
//    /// </param>
//    /// <param name="loggerFactory">Optional logger factory.</param>
//    /// <exception cref="ArgumentNullException"><paramref name="childFactory"/> is <see langword="null"/>.</exception>
//    /// <exception cref="ArgumentException">Serialized state is not a valid composite state.</exception>
//    public CompositeAIContextProvider(
//        JsonElement serializedState,
//        JsonSerializerOptions? jsonSerializerOptions,
//        Func<ChildProviderState, JsonSerializerOptions?, AIContextProvider> childFactory,
//        ILoggerFactory? loggerFactory = null)
//    {
//        _ = Throw.IfNull(childFactory);

//        if (serializedState.ValueKind != JsonValueKind.Object)
//        {
//            throw new ArgumentException("The serialized composite state must be a JSON object.", nameof(serializedState));
//        }

//        var jso = jsonSerializerOptions ?? AgentsAIJsonUtilities.DefaultOptions;

//        var state = serializedState.Deserialize(
//            jso.GetTypeInfo(typeof(CompositeAIContextProviderState))) as CompositeAIContextProviderState;

//        if (state?.Providers is null)
//        {
//            throw new ArgumentException("Serialized composite provider state was missing provider entries.", nameof(serializedState));
//        }

//        var providers = new List<AIContextProvider>(state.Providers.Count);
//        foreach (var childState in state.Providers)
//        {
//            // Delegate construction of each concrete child provider to the caller-supplied factory.
//            providers.Add(childFactory(childState, jsonSerializerOptions));
//        }

//        this._providers = providers;
//        this._logger = loggerFactory?.CreateLogger<CompositeAIContextProvider>();
//    }

//    /// <inheritdoc />
//    public override async ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
//    {
//        _ = Throw.IfNull(context);

//        // If there are no providers, return an empty context.
//        if (this._providers.Count == 0)
//        {
//            return new AIContext();
//        }

//        string? aggregatedInstructions = null;
//        IList<ChatMessage>? aggregatedMessages = null;
//        IList<AITool>? aggregatedTools = null;

//        foreach (var provider in this._providers)
//        {
//            if (provider is null)
//            {
//                continue;
//            }

//            AIContext aiContext;
//            try
//            {
//                aiContext = await provider.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
//            }
//            catch (Exception ex)
//            {
//                this._logger?.LogError(
//                    ex,
//                    "CompositeAIContextProvider: InvokingAsync failed for provider {ProviderType}.",
//                    provider.GetType().FullName);
//                // Skip this provider's contribution, but continue with others.
//                continue;
//            }

//            if (!string.IsNullOrWhiteSpace(aiContext.Instructions))
//            {
//                if (string.IsNullOrWhiteSpace(aggregatedInstructions))
//                {
//                    aggregatedInstructions = aiContext.Instructions;
//                }
//                else
//                {
//                    aggregatedInstructions = $"{aggregatedInstructions}\n{aiContext.Instructions}";
//                }
//            }

//            if (aiContext.Messages is { Count: > 0 })
//            {
//                aggregatedMessages ??= new List<ChatMessage>();
//                foreach (var message in aiContext.Messages)
//                {
//                    aggregatedMessages.Add(message);
//                }
//            }

//            if (aiContext.Tools is { Count: > 0 })
//            {
//                aggregatedTools ??= new List<AITool>();
//                foreach (var tool in aiContext.Tools)
//                {
//                    aggregatedTools.Add(tool);
//                }
//            }
//        }

//        return new AIContext
//        {
//            Instructions = aggregatedInstructions,
//            Messages = aggregatedMessages,
//            Tools = aggregatedTools
//        };
//    }

//    /// <inheritdoc />
//    public override async ValueTask InvokedAsync(InvokedContext context, CancellationToken cancellationToken = default)
//    {
//        _ = Throw.IfNull(context);

//        if (this._providers.Count == 0)
//        {
//            return;
//        }

//        List<Exception>? exceptions = null;

//        foreach (var provider in this._providers)
//        {
//            if (provider is null)
//            {
//                continue;
//            }

//            try
//            {
//                await provider.InvokedAsync(context, cancellationToken).ConfigureAwait(false);
//            }
//            catch (Exception ex)
//            {
//                exceptions ??= [];
//                exceptions.Add(ex);

//                this._logger?.LogError(
//                    ex,
//                    "CompositeAIContextProvider: InvokedAsync failed for provider {ProviderType}.",
//                    provider.GetType().FullName);
//                // Continue notifying remaining providers.
//            }
//        }

//        // Intentionally do not rethrow; the composite should not break the agent run pipeline
//        // because one of the post-invocation hooks failed.
//    }

//    /// <summary>
//    /// Serializes the composite provider, including all inner providers' type and state.
//    /// </summary>
//    /// <param name="jsonSerializerOptions">Serializer options.</param>
//    /// <returns>A <see cref="JsonElement"/> representing the composite provider configuration.</returns>
//    /// <remarks>
//    /// <para>
//    /// Each child provider is serialized as a <see cref="ChildProviderState"/> containing:
//    /// <list type="bullet">
//    /// <item><description><see cref="ChildProviderState.ProviderType"/> – the assembly-qualified type name.</description></item>
//    /// <item><description><see cref="ChildProviderState.SerializedState"/> – the provider's own serialized state, if any.</description></item>
//    /// </list>
//    /// </para>
//    /// Callers are expected to provide a corresponding factory when reconstructing the composite.
//    /// </remarks>
//    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
//    {
//        var jso = jsonSerializerOptions ?? AgentsAIJsonUtilities.DefaultOptions;

//        var childStates = new List<ChildProviderState>(this._providers.Count);

//        foreach (var provider in this._providers)
//        {
//            if (provider is null)
//            {
//                continue;
//            }

//            JsonElement serializedState = default;
//            try
//            {
//                serializedState = provider.Serialize(jsonSerializerOptions);
//            }
//            catch (Exception ex)
//            {
//                this._logger?.LogError(
//                    ex,
//                    "CompositeAIContextProvider: Failed to serialize provider {ProviderType}.",
//                    provider.GetType().FullName);
//                // If serialization fails for a child, record only the type and leave state default.
//            }

//            childStates.Add(new ChildProviderState
//            {
//                ProviderType = provider.GetType().AssemblyQualifiedName,
//                SerializedState = serializedState
//            });
//        }

//        var state = new CompositeAIContextProviderState
//        {
//            Providers = childStates
//        };

//        return JsonSerializer.SerializeToElement(state, jso.GetTypeInfo(typeof(CompositeAIContextProviderState)));
//    }

//    /// <inheritdoc />
//    /// <remarks>
//    /// The composite exposes services from:
//    /// <list type="bullet">
//    /// <item><description>Itself.</description></item>
//    /// <item><description>Each child provider, in order; the first non-<see langword="null"/> service is returned.</description></item>
//    /// </list>
//    /// This allows callers to locate individual providers via <see cref="GetService{TService}(object?)"/> when needed.
//    /// </remarks>
//    public override object? GetService(Type serviceType, object? serviceKey = null)
//    {
//        _ = Throw.IfNull(serviceType);

//        // First, allow base class to return "this" if appropriate.
//        var service = base.GetService(serviceType, serviceKey);
//        if (service is not null)
//        {
//            return service;
//        }

//        foreach (var provider in this._providers)
//        {
//            service = provider.GetService(serviceType, serviceKey);
//            if (service is not null)
//            {
//                return service;
//            }
//        }

//        return null;
//    }

//    /// <summary>
//    /// Represents the serialized state of a single child provider within the composite.
//    /// </summary>
//    public sealed class ChildProviderState
//    {
//        /// <summary>
//        /// Gets or sets the assembly-qualified type name of the provider.
//        /// </summary>
//        public string? ProviderType { get; set; }

//        /// <summary>
//        /// Gets or sets the serialized state of the provider.
//        /// </summary>
//        public JsonElement SerializedState { get; set; }
//    }

//    /// <summary>
//    /// Represents the serialized state of the <see cref="CompositeAIContextProvider"/>.
//    /// </summary>
//    internal sealed class CompositeAIContextProviderState
//    {
//        /// <summary>
//        /// Gets or sets the child provider states in invocation order.
//        /// </summary>
//        public List<ChildProviderState>? Providers { get; set; }
//    }
//}
