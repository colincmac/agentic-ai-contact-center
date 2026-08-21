using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Extensions.AI.Contents;
using Extensions.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Realtime;

/// <summary>
/// Provides an <see cref="AIAgent"/> that delegates to an <see cref="IRealtimeClient"/> implementation
/// for real-time, bidirectional AI interactions.
/// </summary>
/// <remarks>
/// <para>
/// **<b>NOTE: The <see cref="RealtimeAIAgent"/> is still a work in progress and certain functionality hasn't been implemented yet.</b>
/// </para>
/// <para>
/// The <see cref="RealtimeAIAgent"/> bridges the <see cref="AIAgent"/> abstraction with real-time
/// streaming protocols. Unlike <see cref="ChatClientAgent"/>, which uses request/response style interactions,
/// this agent maintains persistent sessions via <see cref="IRealtimeClientSession"/> for continuous
/// bidirectional communication.
/// </para>
/// <para>
/// <strong>Security considerations:</strong> The <see cref="RealtimeAIAgent"/> orchestrates data flow
/// across trust boundaries. The underlying AI service is an external endpoint and real-time responses
/// should be treated as untrusted output. Developers should validate and sanitize output before rendering
/// it in HTML, executing it as code, or passing it to any security-sensitive context.
/// </para>
/// </remarks>
public class RealtimeAIAgent : AIAgent, IRealtimeAgent
{
    private readonly RealtimeAgentOptions? _agentOptions;
    private readonly AIAgentMetadata _agentMetadata;
    private readonly ILogger _logger;
    private readonly HashSet<string> _aiContextProviderStateKeys;


    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAIAgent"/> class.
    /// </summary>
    /// <param name="realtimeClient">The realtime client to use for creating sessions.</param>
    /// <param name="options">Optional configuration options for the agent.</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers used by the agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="realtimeClient"/> is <see langword="null"/>.</exception>
    public RealtimeAIAgent(
        IRealtimeClient realtimeClient,
        RealtimeAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _ = Throw.IfNull(realtimeClient);

        RealtimeClient = realtimeClient;
        _agentOptions = options?.Clone();
        _agentMetadata = new AIAgentMetadata("realtime");
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RealtimeAIAgent>();
        ChatHistoryProvider = _agentOptions?.ChatHistoryProvider ?? new InMemoryChatHistoryProvider();
        AIContextProviders = _agentOptions?.AIContextProviders as IReadOnlyList<AIContextProvider> ?? _agentOptions?.AIContextProviders?.ToList();

        _aiContextProviderStateKeys = ValidateAndCollectStateKeys(_agentOptions?.AIContextProviders, ChatHistoryProvider);
    }

    /// <summary>
    /// Gets the underlying <see cref="IRealtimeClient"/> used by this agent to create realtime sessions.
    /// </summary>
    public IRealtimeClient RealtimeClient { get; }

    /// <summary>
    /// Gets the <see cref="ChatHistoryProvider"/> used by this agent, to support cases where the chat history is not stored by the agent service.
    /// </summary>
    /// <remarks>
    /// This property may be null in case the agent stores messages in the underlying agent service.
    /// </remarks>
    public ChatHistoryProvider? ChatHistoryProvider { get; private set; }

    /// <summary>
    /// Gets the list of <see cref="AIContextProvider"/> instances used by this agent, to support cases where additional context is needed for each agent run.
    /// </summary>
    /// <remarks>
    /// This property may be null in case no additional context providers were configured.
    /// </remarks>
    public IReadOnlyList<AIContextProvider>? AIContextProviders { get; }

    /// <inheritdoc/>
    protected override string? IdCore => _agentOptions?.Id;

    /// <inheritdoc/>
    public override string? Name => _agentOptions?.Name;

    /// <inheritdoc/>
    public override string? Description => _agentOptions?.Description;

    /// <summary>
    /// Gets the default <see cref="RealtimeSessionOptions"/> configured for this agent.
    /// </summary>
    public RealtimeSessionOptions? SessionOptions => _agentOptions?.SessionOptions;



    /// <summary>
    /// Sends a client message to the realtime session.
    /// </summary>
    /// <param name="session">The agent session containing the active realtime client session.</param>
    /// <param name="message">The client message to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session does not have an active realtime client session.</exception>
    public virtual async Task SendAsync(
        RealtimeAIAgentSession session,
        RealtimeClientMessage message,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(session);
        _ = Throw.IfNull(message);

        var clientSession = session.ClientSession
            ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        _logger.LogDebug("Sending message to realtime session for agent '{AgentName}' (Id: {AgentId})", GetLoggingAgentName(), Id);

        await clientSession.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }


    /// <inheritdoc/>
    /// <remarks>
    /// The <see cref="RealtimeAIAgent"/> does not support the standard request/response <see cref="AIAgent.RunAsync"/>
    /// pattern. Use <see cref="CreateSessionAsync"/>, <see cref="SendAsync"/>, and
    /// <see cref="RunCoreStreamingAsync"/> instead.
    /// </remarks>
    /// <exception cref="NotSupportedException">Always thrown. Use the realtime session APIs instead.</exception>
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"{nameof(RealtimeAIAgent)} does not support the standard RunAsync pattern. " +
            $"Use {nameof(CreateSessionAsync)}, {nameof(SendAsync)}, and {nameof(RunCoreStreamingAsync)} for realtime interactions.");
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        (RealtimeAIAgentSession safeSession, RealtimeAgentContinuationToken? continuationToken) = await GetConfiguredSessionAsync(session, options, messages, cancellationToken);

        var clientSession = safeSession.ClientSession ?? throw new InvalidOperationException("The session does not have an active realtime client session. Call CreateRealtimeSessionAsync first.");

        // Update the run context with the resolved session so any downstream classes
        // always have a valid session, even when the caller passed null.
        EnsureRunContextHasSession(safeSession);

        _logger.LogDebug("Starting streaming response from realtime session for agent '{AgentName}' (Id: {AgentId})", GetLoggingAgentName(), Id);
        //List<ChatResponseUpdate> responseUpdates = GetResponseUpdates(continuationToken);


        IAsyncEnumerator<RealtimeServerMessage> responseUpdatesEnumerator;

        try
        {
            // Using the enumerator to ensure we consider the case where no updates are returned for notification.
            responseUpdatesEnumerator = clientSession.GetStreamingResponseAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            await NotifyAIContextProviderOfFailureAsync(safeSession, ex, GetInputMessages([.. messages], continuationToken), cancellationToken).ConfigureAwait(false);
            throw;
        }

        bool hasUpdates;
        try
        {
            // Ensure we start the streaming request
            hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await NotifyAIContextProviderOfFailureAsync(safeSession, ex, GetInputMessages([.. messages], continuationToken), cancellationToken).ConfigureAwait(false);
            throw;
        }

        List<RealtimeServerMessage> responseUpdates = [];
        List<AgentResponseUpdate> turnUpdates = [];

        while (hasUpdates)
        {

            var update = responseUpdatesEnumerator.Current;
            var turnFinished = false;
            if (update is not null)
            {
                responseUpdates.Add(update);
                var wrapped = new AgentResponseUpdate()
                {
                    AgentId = Id,
                    AuthorName = Name,
                    RawRepresentation = update,
                    MessageId = update.MessageId,
                    CreatedAt = DateTime.UtcNow,
                };

                switch (update)
                {
                    case OutputTextAudioRealtimeServerMessage outputMessage:
                        wrapped.ResponseId = outputMessage.ResponseId;
                        if (outputMessage.Text is not null)
                        {
                            wrapped.Contents.Add(new TextContent(outputMessage.Text));
                        }
                        if (outputMessage.Audio is not null)
                        {
                            wrapped.Contents.Add(new DataContent(Convert.FromBase64String(outputMessage.Audio), "audio/pcm"));
                        }
                        break;

                    case ResponseCreatedRealtimeServerMessage responseMessage:
                        wrapped.ResponseId = responseMessage.ResponseId;
                        if (responseMessage.Type == RealtimeServerMessageType.ResponseCreated)
                        {
                            wrapped.Contents.Add(new RealtimeResponseStartContent(responseMessage.ResponseId));
                        }
                        else if (responseMessage.Type == RealtimeServerMessageType.ResponseDone)
                        {
                            wrapped.Contents.Add(new RealtimeResponseFinishedContent(responseMessage.ResponseId));
                            turnFinished = true;
                        }
                        if (responseMessage.Usage is not null)
                        {
                            wrapped.Contents.Add(new UsageContent(responseMessage.Usage));
                        }
                        if (responseMessage.Error is not null)
                        {
                            wrapped.Contents.Add(responseMessage.Error);
                        }
                        break;

                    case ResponseOutputItemRealtimeServerMessage outputItemMessage:
                        wrapped.ResponseId = outputItemMessage.ResponseId;
                        if (wrapped.MessageId is null && outputItemMessage.Item?.Id is { } itemId)
                        {
                            wrapped.MessageId = itemId;
                        }
                        if (outputItemMessage.Item?.Contents is { Count: > 0 } itemContents)
                        {
                            foreach (var content in itemContents)
                            {
                                wrapped.Contents.Add(content);
                            }
                        }

                        break;

                    case InputAudioTranscriptionRealtimeServerMessage transcriptionMessage:
                        if (!string.IsNullOrEmpty(transcriptionMessage.Transcription))
                        {
                            wrapped.Contents.Add(new TextContent(transcriptionMessage.Transcription));
                        }
                        if (transcriptionMessage.Error is not null)
                        {
                            wrapped.Contents.Add(transcriptionMessage.Error);
                        }
                        if (transcriptionMessage.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted
                            && transcriptionMessage.Usage is not null)
                        {
                            wrapped.Contents.Add(new UsageContent(transcriptionMessage.Usage));
                        }
                        break;

                    case InputAudioBufferSpeechRealtimeServerMessage speechMessage:
                        if (wrapped.MessageId is null && speechMessage.ItemId is { } speechItemId)
                        {
                            wrapped.MessageId = speechItemId;
                        }
                        if (speechMessage.Type == InputAudioBufferSpeechRealtimeServerMessage.InputAudioBufferSpeechStarted)
                        {
                            wrapped.Contents.Add(new RealtimeVadContent(VadEventType.InputSpeechStarted)
                            {
                                StartTime = speechMessage.AudioStart,
                            });
                        }
                        else if (speechMessage.Type == InputAudioBufferSpeechRealtimeServerMessage.InputAudioBufferSpeechStopped)
                        {
                            wrapped.Contents.Add(new RealtimeVadContent(VadEventType.InputSpeechEnded)
                            {
                                EndTime = speechMessage.AudioEnd,
                            });
                        }
                        break;

                    default:
                        // Fallback (including RawContentOnly and error-mapped messages):
                        // yield with RawRepresentation only.
                        break;
                }
                turnUpdates.Add(wrapped);
                yield return wrapped;

                hasUpdates = await responseUpdatesEnumerator.MoveNextAsync().ConfigureAwait(false);

                if (turnFinished)
                {
                    // TODO use AI Context Provider to store finished turns, and yield them on new calls with a continuation token, instead of relying on the client to keep track of turn updates with the continuation token.
                    //await NotifyAIContextProviderOfSuccessAsync();
                    turnFinished = false;
                }
            }
        }
    }




    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        return base.GetService(serviceType, serviceKey)
            ?? (serviceKey is null && serviceType == typeof(AIAgentMetadata) ? _agentMetadata
            : serviceType == typeof(IRealtimeClient) ? RealtimeClient
            : serviceType == typeof(RealtimeAgentOptions) ? _agentOptions
            : this.AIContextProviders?.Select(provider => provider.GetService(serviceType, serviceKey)).FirstOrDefault(s => s is not null)
            ?? this.ChatHistoryProvider?.GetService(serviceType, serviceKey)
            ?? RealtimeClient.GetService(serviceType, serviceKey));
    }

    /// <summary>
    /// Creates a new realtime session via the underlying <see cref="IRealtimeClient"/> and returns
    /// an <see cref="RealtimeAIAgentSession"/> that wraps it.
    /// </summary>
    /// <param name="sessionOptions">Optional session options that override the agent's defaults.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="RealtimeAIAgentSession"/> containing the active realtime session.</returns>
    public virtual async ValueTask<RealtimeAIAgentSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = sessionOptions ?? _agentOptions?.SessionOptions;

        var clientSession = await RealtimeClient.CreateSessionAsync(effectiveOptions, cancellationToken).ConfigureAwait(false);

        var session = new RealtimeAIAgentSession
        {
            ClientSession = clientSession,
        };
        return session;
    }

    /// <inheritdoc/>
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var clientSession = await RealtimeClient.CreateSessionAsync(_agentOptions?.SessionOptions, cancellationToken).ConfigureAwait(false);
        return new RealtimeAIAgentSession()
        {
            ClientSession = clientSession,
        };
    }

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (session is not RealtimeAIAgentSession realtimeSession)
        {
            throw new InvalidOperationException(
                $"The provided session is of type '{session.GetType().Name}', but {nameof(RealtimeAIAgent)} requires a session of type '{nameof(RealtimeAIAgentSession)}'.");
        }

        return new(realtimeSession.Serialize(jsonSerializerOptions));
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new(RealtimeAIAgentSession.Deserialize(serializedState, jsonSerializerOptions));
    }

    private static RealtimeAgentContinuationToken? WrapContinuationToken(ResponseContinuationToken? continuationToken, IEnumerable<ChatMessage>? inputMessages = null, List<ChatResponseUpdate>? responseUpdates = null)
    {
        if (continuationToken is null)
        {
            return null;
        }

        return new(continuationToken)
        {
            // Save input messages to the continuation token so they can be added to the session and
            // provided to the context provider in the last successful streaming resumption run.
            // That's necessary for scenarios where initial streaming run is interrupted and streaming is resumed later.
            InputMessages = inputMessages?.Any() is true ? inputMessages : null,

            // Save all updates received so far to the continuation token so they can be provided to the
            // message store and context provider in the last successful streaming resumption run.
            // That's necessary for scenarios where a streaming run is interrupted after some updates were received.
            ResponseUpdates = responseUpdates?.Count > 0 ? responseUpdates : null
        };
    }


    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> when an agent run succeeded, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private async ValueTask NotifyAIContextProviderOfSuccessAsync(
        RealtimeAIAgentSession session,
        IEnumerable<ChatMessage> inputMessages,
        IEnumerable<ChatMessage> responseMessages,
        CancellationToken cancellationToken)
    {
        if (this.AIContextProviders is { Count: > 0 } contextProviders)
        {
            try
            {
#pragma warning disable MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                AIContextProvider.InvokedContext invokedContext = new AIContextProvider.InvokedContext(this, session, inputMessages, responseMessages);
#pragma warning restore MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                foreach (var contextProvider in contextProviders)
                {
                    await contextProvider.InvokedAsync(invokedContext, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Log and swallow exceptions from the context provider to avoid impacting the agent's execution.
                // The context provider should handle its own exceptions and not let them propagate up to the agent.
            }
        }
    }

    /// <summary>
    /// Notify the <see cref="AIContextProvider"/> of any failure during an agent run, if there is an <see cref="AIContextProvider"/>.
    /// </summary>
    private async ValueTask NotifyAIContextProviderOfFailureAsync(
        RealtimeAIAgentSession session,
        Exception ex,
        IEnumerable<ChatMessage>? inputMessages = null,
        CancellationToken cancellationToken = default)
    {
        if (this.AIContextProviders is { Count: > 0 } contextProviders)
        {
            AIContextProvider.InvokedContext invokedContext = new(this, session, inputMessages ?? [], ex);

            foreach (var contextProvider in contextProviders)
            {
                await contextProvider.InvokedAsync(invokedContext, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<ChatMessage> GetInputMessages(IReadOnlyCollection<ChatMessage> inputMessages, RealtimeAgentContinuationToken? token)
    {
        // First, use input messages if provided.
        if (inputMessages.Count > 0)
        {
            return inputMessages;
        }

        // Fallback to messages saved in the continuation token if available.
        return token?.InputMessages ?? [];
    }

    private static List<ChatResponseUpdate> GetResponseUpdates(RealtimeAgentContinuationToken? token)
    {
        // Restore any previously received updates from the continuation token.
        return token?.ResponseUpdates?.ToList() ?? [];
    }

    private string GetLoggingAgentName() => Name ?? "AnonymousAgent";

    /// <summary>
    /// Validates that all configured providers have unique <see cref="AIContextProvider.StateKeys"/> values
    /// and returns a <see cref="HashSet{T}"/> of the AIContextProvider state keys.
    /// </summary>
    private static HashSet<string> ValidateAndCollectStateKeys(IEnumerable<AIContextProvider>? aiContextProviders, ChatHistoryProvider? chatHistoryProvider)
    {
        HashSet<string> stateKeys = new(StringComparer.Ordinal);

        if (aiContextProviders is not null)
        {
            foreach (var provider in aiContextProviders)
            {
                foreach (var key in provider.StateKeys)
                {
                    if (!stateKeys.Add(key))
                    {
                        throw new InvalidOperationException(
                            $"Multiple providers use the same state key '{key}'. Each provider must use a unique state key to avoid overwriting each other's state.");
                    }
                }
            }
        }

        if (chatHistoryProvider is null
            && stateKeys.Contains(nameof(InMemoryChatHistoryProvider)))
        {
            throw new InvalidOperationException(
                $"The default {nameof(InMemoryChatHistoryProvider)} uses the state key '{nameof(InMemoryChatHistoryProvider)}', which is already used by one of the configured AIContextProviders. Each provider must use a unique state key to avoid overwriting each other's state. To resolve this, either configure a different state key for the AIContextProvider that is using '{nameof(InMemoryChatHistoryProvider)}' as its state key, or provide a custom ChatHistoryProvider with a unique state key.");
        }

        if (chatHistoryProvider is not null)
        {
            foreach (var key in chatHistoryProvider.StateKeys)
            {
                if (stateKeys.Contains(key))
                {
                    throw new InvalidOperationException(
                        $"The ChatHistoryProvider '{chatHistoryProvider.GetType().Name}' uses state key '{key}' which is already used by one of the configured AIContextProviders. Each provider must use unique state keys to avoid overwriting each other's state. To resolve this, either configure different state keys for the AIContextProvider that shares keys with the ChatHistoryProvider, or reconfigure the custom ChatHistoryProvider with unique state keys.");
                }
            }
        }

        return stateKeys;
    }

    private async Task<(RealtimeAIAgentSession session, RealtimeAgentContinuationToken? continuationToken)> GetConfiguredSessionAsync(
               AgentSession? agentSession,
               AgentRunOptions? runOptions,
               IEnumerable<ChatMessage> initialMessages,
               CancellationToken cancellationToken)
    {
        var (sessionOptions, continuationToken) = GetSessionConfiguration(runOptions);

        //var client = ApplyRunOptionsTransformationsToClient(runOptions, RealtimeClient);
        agentSession ??= await this.CreateSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);

        if (agentSession is not RealtimeAIAgentSession typedSession)
        {
            throw new InvalidOperationException($"The provided session type '{agentSession.GetType().Name}' is not compatible with this agent. Only sessions of type '{nameof(RealtimeAIAgentSession)}' can be used by this agent.");
        }

        return (typedSession, continuationToken);
    }

    private static IRealtimeClient ApplyRunOptionsTransformationsToClient(RealtimeAgentRunOptions? options, IRealtimeClient conversationClient)
    {
        if (options?.RealtimeClientFactory is not null)
        {
            // If we have a custom chat client factory, we should use it to create a new chat client with the transformed tools.
            conversationClient = options.RealtimeClientFactory(conversationClient);
            _ = Throw.IfNull(conversationClient);
        }

        return conversationClient;
    }


    private (RealtimeSessionOptions?, RealtimeAgentContinuationToken?) GetSessionConfiguration(AgentRunOptions? runOptions = null)
    {
        var requestOptions = (runOptions as RealtimeAgentRunOptions)?.SessionOptions;
        if (_agentOptions?.SessionOptions is null)
        {
            return ApplyAgentRunOptionsOverrides(requestOptions, runOptions);
        }

        if (requestOptions is null)
        {
            return ApplyAgentRunOptionsOverrides(_agentOptions?.SessionOptions, runOptions);
        }
        var instructions = !string.IsNullOrWhiteSpace(requestOptions.Instructions) && !string.IsNullOrWhiteSpace(_agentOptions.SessionOptions.Instructions)
            ? $"{_agentOptions.SessionOptions.Instructions}{Environment.NewLine}{requestOptions.Instructions}"
            : (!string.IsNullOrWhiteSpace(requestOptions.Instructions)
            ? requestOptions.Instructions
            : _agentOptions.SessionOptions.Instructions);



        // Combine options, giving precedence to requestOptions
        var finalOptions = new RealtimeSessionOptions 
        {
            Instructions = instructions,
            VoiceActivityDetection = requestOptions.VoiceActivityDetection ?? _agentOptions.SessionOptions.VoiceActivityDetection,
            Voice = requestOptions.Voice ?? _agentOptions.SessionOptions.Voice,
            InputAudioFormat = requestOptions.InputAudioFormat ?? _agentOptions.SessionOptions.InputAudioFormat,
            OutputAudioFormat = requestOptions.OutputAudioFormat ?? _agentOptions.SessionOptions.OutputAudioFormat,
            ToolMode = requestOptions.ToolMode ?? _agentOptions.SessionOptions.ToolMode,
            MaxOutputTokens = requestOptions.MaxOutputTokens,
            Model = requestOptions.Model ?? _agentOptions.SessionOptions.Model,
            OutputModalities = requestOptions.OutputModalities ?? _agentOptions.SessionOptions.OutputModalities,
            SessionKind = RealtimeSessionKind.Conversation,
            TranscriptionOptions = requestOptions.TranscriptionOptions ?? _agentOptions.SessionOptions.TranscriptionOptions,
            Tools = requestOptions.Tools ?? _agentOptions.SessionOptions.Tools,
        };


        return ApplyAgentRunOptionsOverrides(finalOptions, runOptions);

        static (RealtimeSessionOptions?, RealtimeAgentContinuationToken?) ApplyAgentRunOptionsOverrides(RealtimeSessionOptions? realtimeSessionOptions, AgentRunOptions? agentRunOptions)
        {

            RealtimeAgentContinuationToken? agentContinuationToken = null;

            if (agentRunOptions?.ContinuationToken is { } continuationToken)
            {
                agentContinuationToken = RealtimeAgentContinuationToken.FromToken(continuationToken);
                realtimeSessionOptions ??= new RealtimeSessionOptions();
            }

            return (realtimeSessionOptions, agentContinuationToken);
        }
    }
    /// <summary>
    /// Ensures that <see cref="AIAgent.CurrentRunContext"/> contains the resolved session.
    /// </summary>
    /// <remarks>
    /// The base class sets <see cref="AIAgent.CurrentRunContext"/> with the raw session parameter
    /// (which may be null) and restores it after each yield in streaming scenarios. After
    /// <see cref="PrepareSessionAndMessagesAsync"/> resolves or creates a session, we update the
    /// context so the <see cref="ServiceStoredSimulatingChatClient"/> decorator always has a valid session.
    /// The original agent from the context is preserved to maintain the top-of-stack agent in
    /// decorated agent scenarios.
    /// </remarks>
    private static void EnsureRunContextHasSession(RealtimeAIAgentSession safeSession)
    {
        var context = CurrentRunContext;
        if (context is not null && context.Session != safeSession)
        {
            CurrentRunContext = new(context.Agent, safeSession, context.RequestMessages, context.RunOptions);
        }
    }
}
