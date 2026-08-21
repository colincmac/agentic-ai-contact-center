//using System.Runtime.CompilerServices;
//using System.Text.Json;
//using Extensions.AI.Contents;
//using Extensions.AI.RealtimeVoice;
//using Extensions.AI.RealtimeVoice.Configuration;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Microsoft.Shared.Diagnostics;
//using OpenAI.Realtime;
//using RealtimeAudioFormat = OpenAI.Realtime.RealtimeAudioFormat;
//namespace Extensions.AI.RealtimeVoice.OpenAI;



///// <summary>
///// The Session object, which controls the parameters of the interaction, like the model being used, the voice used to generate output, and other configuration.
///// A Conversation, which represents user input Items and model output Items generated during the current session.
///// Responses, which are model-generated audio or text Items that are added to the Conversation.
///// An OpenAI session has a max duration of 30minutes
///// </summary>
//public sealed class OpenAIRealtimeConversationSession : ILiveConversationSession
//{
//    private RealtimeSessionClient? _sessionClient;
//    private readonly string _realtimeModelId;

//    private readonly OpenAIRealtimeConversationClient _parentClient;
//    private readonly ILogger _logger;
//    private readonly SemaphoreSlim _sendLock = new(1, 1);
//    private Task<RealtimeSessionClient>? _sessionInitializationTask;

//    private LiveConversationSessionOptions? _options;
//    private readonly TaskCompletionSource<string?> _sessionStartTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
//    private RealtimeSessionState _state = RealtimeSessionState.Connecting;
//    private bool _disposed;
//    public OpenAIRealtimeConversationSession(
//        OpenAIRealtimeConversationClient parentClient,
//        string realtimeModelId,
//        LiveConversationSessionOptions? options = null,
//        ILogger? logger = null)
//    {
//        _parentClient = Throw.IfNull(parentClient);
//        _options = options;
//        _state = RealtimeSessionState.None;

//        _logger = logger ?? NullLogger.Instance;
//        Metadata = new(realtimeModelId);
//        _realtimeModelId = realtimeModelId;
//    }

//    internal OpenAIRealtimeConversationSession(
//        OpenAIRealtimeConversationClient parentClient,
//        RealtimeSessionClient sessionClient,
//        string realtimeModelId,
//        LiveConversationSessionOptions? options = null,
//        ILogger? logger = null)
//    {
//        _parentClient = Throw.IfNull(parentClient);
//        _sessionClient = Throw.IfNull(sessionClient);
//        _options = options;
//        _state = RealtimeSessionState.Connecting;

//        _logger = logger ?? NullLogger.Instance;
//        Metadata = new(realtimeModelId);
//        _realtimeModelId = realtimeModelId;

//    }

//    public LiveConversationSessionMetadata Metadata { get; }
//    public LiveConversationSessionOptions? CurrentSessionConfiguration => _options;

//    private string? _sessionId;
//    /// <inheritdoc/>
//    public string? SessionId => _sessionId;

//    /// <inheritdoc/>
//    public RealtimeSessionState State => _state;

//    /// <inheritdoc/>
//    public event EventHandler<RealtimeSessionStateChangedEventArgs>? StateChanged;

//    public IList<AITool> SessionTools => _options?.Tools ?? [];

//    public async Task StartResponseAsync(LiveConversationResponseOptions? responseOptions, CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        var sessionResponseOptions = ToOpenAISessionResponseOptions(responseOptions);
//        await session.StartResponseAsync(sessionResponseOptions, cancellationToken)
//            .ConfigureAwait(false);
//    }

//    /// <inheritdoc/>
//    public async Task SendAudioAsync(
//        ReadOnlyMemory<byte> audioData,
//        CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
//        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
//        try
//        {

//            await session.SendInputAudioAsync(BinaryData.FromBytes(audioData.ToArray()), cancellationToken)
//                .ConfigureAwait(false);
//        }
//        finally
//        {
//            _sendLock.Release();
//        }
//    }

//    /// <inheritdoc/>
//    public async Task SendAudioStreamAsync(
//        Stream audioStream,
//        CancellationToken cancellationToken = default)
//    {
       
//        Throw.IfNull(audioStream);
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        await session.SendInputAudioAsync(
//            audioStream, cancellationToken).ConfigureAwait(false);
//    }

//    /// <inheritdoc/>
//    public async Task SendMessagesAsync(
//        IEnumerable<ChatMessage> messages,
//        CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        foreach (var message in messages)
//        {
//            if (ToRealtimeItem(message) is not { } realtimeItem)
//            {
//                continue;
//            }
//            await session.AddItemAsync(realtimeItem, cancellationToken).ConfigureAwait(false);
//        }
//    }

//    /// <inheritdoc/>

//    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(LiveConversationResponseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        var sessionResponseOptions = ToOpenAISessionResponseOptions(options);

//        if (sessionResponseOptions is not null)
//        {
//            await session.StartResponseAsync(sessionResponseOptions, cancellationToken)
//                .ConfigureAwait(false);
//        }

//        await foreach (var update in FromOpenAIStreamingRealtimeUpdatesAsync(session.ReceiveUpdatesAsync(cancellationToken), cancellationToken))
//        {
//            if (update.RawRepresentation is ConversationSessionStartedUpdate started)
//            {
//                _sessionId = started.SessionId;
//                _sessionStartTcs.TrySetResult(_sessionId);
//                UpdateSessionState(RealtimeSessionState.Connected);
//            }
//            yield return update;
//        }
//    }


//    internal static async IAsyncEnumerable<ChatResponseUpdate> FromOpenAIStreamingRealtimeUpdatesAsync(
//        IAsyncEnumerable<RealtimeUpdate> streamingRealtimeUpdates,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        DateTimeOffset? createdAt = null;
//        string? responseId = null;
//        string? conversationId = null;
//        string? sessionId = null;
//        string? modelId = null;
//        string? lastMessageId = null;
//        ChatRole? lastRole = null;
//        HashSet<string> unHandledFunctionCalls = [];

//        // Local helper to construct updates with current accumulated state.
//        ChatResponseUpdate CreateUpdate(List<AIContent>? contents = null)
//        {
//            var update = new ChatResponseUpdate(lastRole, contents)
//            {
//                ConversationId = conversationId,
//                CreatedAt = createdAt,
//                MessageId = lastMessageId,
//                ModelId = modelId,
//                RawRepresentation = null, // set below after switch when we have streamingUpdate
//                ResponseId = responseId,
//            };

//            return update;
//        }

//        await foreach (var streamingUpdate in streamingRealtimeUpdates.WithCancellation(cancellationToken).ConfigureAwait(false))
//        {
//            switch (streamingUpdate)
//            {
//                case ConversationSessionStartedUpdate sessionStarted:
//                    {
//                        lastRole = ChatRole.Assistant;
//                        modelId = sessionStarted.Model;
//                        conversationId ??= sessionStarted.SessionId;
//                        sessionId ??= sessionStarted.SessionId;
//                        createdAt ??= DateTimeOffset.UtcNow;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = sessionStarted;
//                        yield return update;
//                        break;
//                    }

//                case ConversationSessionConfiguredUpdate sessionConfigured:
//                    {
//                        // May update model or other session parameters mid-stream.
//                        // TODO: sessionConfigured.Session?.Model
//                        modelId = sessionConfigured.Model;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = sessionConfigured;
//                        yield return update;
//                        break;
//                    }

//                case TranscriptionSessionConfiguredUpdate transcriptionConfigured:
//                    {
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = transcriptionConfigured;
//                        yield return update;
//                        break;
//                    }

//                case ResponseStartedUpdate responseStarted:
//                    {
//                        responseId = responseStarted.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate();
//                        update.RawRepresentation = responseStarted;

//                        foreach (var item in responseStarted.CreatedItems)
//                        {
//                            if (!string.IsNullOrEmpty(item.FunctionCallId) && item.GetFunctionCallContent() is FunctionCallContent fcc)
//                            {
//                                update.Contents.Add(fcc);
//                            }
//                        }
//                        yield return update;
//                        break;
//                    }

//                case OutputStreamingStartedUpdate outputStreamStarted:
//                    {
//                        // Marks beginning of a new message (output item).
//                        lastMessageId = outputStreamStarted.ItemId;
//                        responseId = outputStreamStarted.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = outputStreamStarted;
//                        yield return update;
//                        break;
//                    }

//                case OutputDeltaUpdate delta:
//                    {
//                        //   delta.Delta?.Text
//                        //   delta.Delta?.AudioTranscript
//                        //   delta.Delta?.Audio (binary / base64)
//                        //   delta.Delta?.FunctionCallArguments

//                        // Ensure we have a message id for the item being updated.
//                        lastMessageId = delta.ItemId;
//                        responseId = delta.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        List<AIContent> contents = [];
//                        if (delta.AudioBytes is { Length: > 0 })
//                        {
//                            contents.Add(new DataContent(delta.AudioBytes, "audio/pcm"));
//                        }

//                        if (!string.IsNullOrEmpty(delta.AudioTranscript))
//                        {
//                            contents.Add(new AudioTranscriptionContent(delta.AudioTranscript));
//                        }

//                        if(!string.IsNullOrEmpty(delta.Text))
//                        {
//                            contents.Add(new TextContent(delta.Text));
//                        }

//                        var update = CreateUpdate(contents);
//                        update.RawRepresentation = delta;
//                        yield return update;

//                        break;
//                    }

//                case OutputTextFinishedUpdate textFinished:
//                    {
//                        lastMessageId = textFinished.ItemId;
//                        responseId = textFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate([new TextContent(textFinished.Text)]);
//                        update.RawRepresentation = textFinished;
//                        yield return update;
//                        break;
//                    }

//                case OutputAudioTranscriptionFinishedUpdate audioTranscriptFinished:
//                    {
//                        lastMessageId = audioTranscriptFinished.ItemId;
//                        responseId = audioTranscriptFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate([new TextContent(audioTranscriptFinished.Transcript)]);
//                        update.RawRepresentation = audioTranscriptFinished;
//                        yield return update;
//                        break;
//                    }

//                case OutputAudioFinishedUpdate audioFinished:
//                    {
//                        lastMessageId = audioFinished.ItemId;
//                        responseId = audioFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = audioFinished;
//                        yield return update;
//                        break;
//                    }

//                case OutputPartFinishedUpdate partFinished:
//                    {
//                        lastMessageId = partFinished.ItemId;
//                        responseId = partFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        // A generic "part done" marker; may or may not have final data.
//                        var update = CreateUpdate();
//                        update.RawRepresentation = partFinished;
//                        yield return update;
//                        break;
//                    }

//                case OutputStreamingFinishedUpdate outputStreamFinished:
//                    {
//                        lastMessageId = outputStreamFinished.ItemId;
//                        responseId = outputStreamFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate();

//                        if (!string.IsNullOrEmpty(outputStreamFinished.FunctionName) && outputStreamFinished.GetFunctionCallContent() is FunctionCallContent functionCall)
//                        {
//                            unHandledFunctionCalls.Add(functionCall.CallId);
//                            update.Contents.Add(functionCall);
//                        }

//                        update.RawRepresentation = outputStreamFinished;
//                        yield return update;
//                        break;
//                    }

//                case ResponseFinishedUpdate responseFinished:
//                    {
//                        responseId = responseFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate();


//                        update.FinishReason = ToFinishReason(responseFinished.StatusDetails?.IncompleteReason) ?? (unHandledFunctionCalls.Count != 0 ? ChatFinishReason.ToolCalls :
//                        ChatFinishReason.Stop);
//                        update.Contents.Add(new UsageContent(responseFinished.Usage.ToUsageDetails()));
//                        update.Contents.Add(new RealtimeResponseFinishedContent());
//                        update.RawRepresentation = responseFinished;

//                        yield return update;
//                        break;
//                    }

//                case ItemCreatedUpdate itemCreated:
//                    {

//                        lastMessageId = itemCreated.ItemId;
//                        lastRole = ToChatRole(itemCreated.MessageRole);
//                        if (!string.IsNullOrEmpty(itemCreated.FunctionCallOutput) && !string.IsNullOrEmpty(itemCreated.FunctionCallId))
//                        {
//                            unHandledFunctionCalls.Remove(itemCreated.FunctionCallId);
//                        }
//                        var update = CreateUpdate();
//                        update.RawRepresentation = itemCreated;

//                        yield return update;
//                        break;
//                    }

//                case ItemDeletedUpdate itemDeleted:
//                    {
//                        lastMessageId = itemDeleted.ItemId;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = itemDeleted;
//                        yield return update;
//                        break;
//                    }

//                case ItemTruncatedUpdate itemTruncated:
//                    {
//                        lastMessageId = itemTruncated.ItemId;
//                        var update = CreateUpdate([new AudioTruncatedContent(itemTruncated.ItemId, itemTruncated.AudioEndMs)]);
//                        update.RawRepresentation = itemTruncated;
//                        yield return update;
//                        break;
//                    }

//                case InputAudioSpeechStartedUpdate speechStarted:
//                    {
//                        lastMessageId = speechStarted.ItemId;
//                        lastRole = ChatRole.User;
//                        var update = CreateUpdate([
//                            new AudioTranscriptionContent(referenceItemId: speechStarted.ItemId) { StartTime = speechStarted.AudioStartTime },
//                            new RealtimeVadContent(VadEventType.InputSpeechStarted) { StartTime = speechStarted.AudioStartTime }
//                            ]);
//                        update.RawRepresentation = speechStarted;
    
//                        yield return update;
//                        break;
//                    }

//                case InputAudioTranscriptionDeltaUpdate inputAudioTxDelta:
//                    {
//                        if (!string.IsNullOrEmpty(inputAudioTxDelta.Delta))
//                        {
//                            lastMessageId = inputAudioTxDelta.ItemId;
//                            lastRole = ChatRole.User;

//                            var update = CreateUpdate([new AudioTranscriptionContent(text: inputAudioTxDelta.Delta, referenceItemId: inputAudioTxDelta.ItemId, referenceContentIndex: inputAudioTxDelta.ContentIndex)]);
//                            update.RawRepresentation = inputAudioTxDelta;
//                            yield return update;
//                        }
//                        break;
//                    }

//                case InputAudioTranscriptionFinishedUpdate inputAudioTxFinished:
//                    {
//                        if (!string.IsNullOrEmpty(inputAudioTxFinished.Transcript))
//                        {
//                            lastMessageId = inputAudioTxFinished.ItemId;
//                            lastRole = ChatRole.User;
//                            var update = CreateUpdate([new TextContent(inputAudioTxFinished.Transcript)]);
//                            update.RawRepresentation = inputAudioTxFinished;
//                            yield return update;
//                        }
//                        break;
//                    }

//                case InputAudioTranscriptionFailedUpdate inputAudioTxFailed:
//                    {
//                        var update = CreateUpdate([new ErrorContent(inputAudioTxFailed.ErrorMessage) {
//                            ErrorCode = inputAudioTxFailed.ErrorCode,
//                            Details = inputAudioTxFailed.ErrorCode
//                        }]);
//                        update.RawRepresentation = inputAudioTxFailed;

//                        yield return update;
//                        break;
//                    }

//                case InputAudioCommittedUpdate inputAudioCommitted:
//                    {
//                        var update = CreateUpdate();
//                        update.RawRepresentation = inputAudioCommitted;
//                        yield return update;
//                        break;
//                    }

//                case InputAudioClearedUpdate inputAudioCleared:
//                    {
//                        var update = CreateUpdate();
//                        update.RawRepresentation = inputAudioCleared;
//                        yield return update;
//                        break;
//                    }

//                case InputAudioSpeechFinishedUpdate speechFinished:
//                    {
//                        lastMessageId = speechFinished.ItemId;
//                        lastRole = ChatRole.User;
//                        var update = CreateUpdate([
//                            new RealtimeVadContent(VadEventType.InputSpeechEnded)
//                            {
//                                EndTime = speechFinished.AudioEndTime
//                            }
//                            ]);
//                        update.RawRepresentation = speechFinished;
//                        yield return update;
//                        break;
//                    }

//                case RateLimitsUpdate rateLimits:
//                    {
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = rateLimits;

//                        yield return update;
//                        break;
//                    }

//                case RealtimeErrorUpdate errorUpdate:
//                    {
//                        var update = CreateUpdate([new ErrorContent(errorUpdate.Message) {
//                            ErrorCode = errorUpdate.ErrorCode,
//                        }]);
//                        update.RawRepresentation = errorUpdate;

//                        yield return update;
//                        break;
//                    }

//                default:
//                    {
//                        // Future-proof: unknown subclass we didn’t explicitly handle.
//                        var update = CreateUpdate();
//                        update.RawRepresentation = streamingUpdate;
//                        yield return update;
//                        break;
//                    }
//            }
//        }
//    }

//    private static ChatFinishReason? ToFinishReason(ConversationIncompleteReason? reason) =>
//     reason switch
//     {
//        var r when r == ConversationIncompleteReason.MaxOutputTokens => ChatFinishReason.Length,
//        var r when r == ConversationIncompleteReason.TurnDetected => ChatFinishReason.Stop,
//        var r when r == ConversationIncompleteReason.ClientCancelled => ChatFinishReason.Stop,
//        var r when r == ConversationIncompleteReason.ContentFilter => ChatFinishReason.ContentFilter,
//         _ => null
//     };
//    private static ChatRole ToChatRole(ConversationMessageRole? role) =>
//    role switch
//    {
//        var r when r == ConversationMessageRole.System => ChatRole.System,
//        var r when r == ConversationMessageRole.User => ChatRole.User,
//        _ => ChatRole.Assistant,
//    };

//    /// <inheritdoc/>
//    public async Task InterruptAsync(CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        await session.CancelResponseAsync(cancellationToken)
//            .ConfigureAwait(false);
//    }

//    /// <inheritdoc/>
//    public async Task CommitPendingAudioAsync(CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        await session.CommitPendingAudioAsync(cancellationToken)
//            .ConfigureAwait(false);
//    }

//    /// <inheritdoc/>
//    public async Task ClearInputAudioAsync(CancellationToken cancellationToken = default)
//    {
       
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

//        await session.ClearInputAudioAsync(cancellationToken)
//            .ConfigureAwait(false);
//    }

//    /// <inheritdoc/>
//    public async Task ConfigureSessionAsync(
//        LiveConversationSessionOptions options,
//        CancellationToken cancellationToken = default)
//    {
       
//        Throw.IfNull(options);
//        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
//        var sessionOptions = ToOpenAISessionOptions(options);
//        await session.ConfigureConversationSessionAsync(sessionOptions, cancellationToken)
//            .ConfigureAwait(false);
//        // Update local options
//        _options = options;
//    }

//    /// <inheritdoc/>
//    public object? GetService(Type serviceType, object? serviceKey = null)
//    {
//        Throw.IfNull(serviceType);

//        return serviceKey is null
//            && (serviceType == typeof(ILiveConversationSession)
//                || serviceType == typeof(OpenAIRealtimeConversationSession))
//            ? this
//            : serviceType == typeof(RealtimeSession)
//            ? _session
//            : null;
//    }

//    /// <inheritdoc/>
//    public void Dispose()
//    {
//        if (_disposed)
//        {
//            return;
//        }

//        _disposed = true;
//        UpdateSessionState(RealtimeSessionState.Closing);

//        try
//        {
//            _sendLock?.Dispose();
//            _session?.Dispose();
//            UpdateSessionState(RealtimeSessionState.Closed);
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Error disposing session {SessionId}", SessionId);
//            UpdateSessionState(RealtimeSessionState.Error, new ErrorContent(ex.Message));
//        }
//    }

//    private Task<RealtimeSessionClient> EnsureSessionAsync(CancellationToken cancellationToken)
//    {
//        ThrowIfDisposed();

//        // Fast path if already initialized.
//        var existingTask = _sessionInitializationTask;
//        if (existingTask is not null)
//        {
//            return existingTask;
//        }

//        // Slow path: create and attempt to publish.
//        return InitializeSessionSlowAsync(cancellationToken);

//        async Task<RealtimeSessionClient> InitializeSessionSlowAsync(CancellationToken ct)
//        {
//            var newTask = CreateSessionAsync(ct);

//            // Publish only if still null; otherwise use the winner.
//            var winner = Interlocked.CompareExchange(ref _sessionInitializationTask, newTask, null);
//            if (winner is not null)
//            {
//                return await winner.ConfigureAwait(false);
//            }
//            return await newTask.ConfigureAwait(false);
//        }
//    }

//    private async Task<RealtimeSessionClient> CreateSessionAsync(CancellationToken cancellationToken)
//    {
//        UpdateSessionState(RealtimeSessionState.Connecting);

//        var realtimeClient = _parentClient.GetService<RealtimeClient>();
//        if (realtimeClient is null)
//        {
//            throw new InvalidOperationException("Cannot create session because the parent client does not have a RealtimeClient service.");
//        }

//        var session = await realtimeClient.StartConversationSessionAsync(_realtimeModelId, cancellationToken: cancellationToken).ConfigureAwait(false);
//        _sessionClient = session;

//        var initialOptions = ToOpenAISessionOptions(_options);
//        if (initialOptions is not null)
//        {
//            await session.ConfigureConversationSessionAsync(initialOptions, cancellationToken).ConfigureAwait(false);
//        }

//        AttachStartupListener(session);
//        return session;
//    }

//    private void AttachStartupListener(RealtimeSessionClient session)
//    {
//        _ = Task.Run(async () =>
//        {
//            try
//            {
//                await foreach (var update in session.ReceiveUpdatesAsync().ConfigureAwait(false))
//                {
//                    if (update is RealtimeServerUpdateSessionCreated started)
//                    {
//                        var session = started.Session as RealtimeConversationSession;
//                        _sessionId = Guid.NewGuid().ToString();
//                        UpdateSessionState(RealtimeSessionState.Connected);
//                        break;
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Failed to establish connection for session {SessionId}", SessionId);
//                UpdateSessionState(RealtimeSessionState.Error, new ErrorContent(ex.Message), reason: "Failed to connect");
//            }
//        });
//    }

//    private static RealtimeItem? ToRealtimeItem(ChatMessage message)
//    {
//        if (message.RawRepresentation is RealtimeItem rawItem)
//        {
//            return rawItem;
//        }

//        RealtimeItem? item;

//        // TODO: Do we need to handle FunctionCallContent here as well?
//        if (message.Role == ChatRole.Tool
//            && message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } resultContent)
//        {
//            string? result = resultContent.Result as string;
//            if (result is null && resultContent.Result is not null)
//            {
//                try
//                {
//                    result = JsonSerializer.Serialize(resultContent.Result, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
//                }
//                catch (NotSupportedException)
//                {
//                    // If the type can't be serialized, skip it.
//                }
//            }

//            item = RealtimeItem.CreateFunctionCallOutput(resultContent.CallId, result);
//        }
//        else
//        {
//            //var contents = message.Contents
//            //    .Select(c => ToConversationContentPart(c, message.Role))
//            //    .Where(c => c != null);
//            item = message switch
//            {
//                { Role: var role } when role == ChatRole.User => RealtimeItem.CreateUserMessage([message.Text]),
//                { Role: var role } when role == ChatRole.Assistant => RealtimeItem.CreateAssistantMessage([message.Text]),
//                { Role: var role } when role == ChatRole.System => RealtimeItem.CreateSystemMessage([message.Text]),
//                _ => null
//            };
//        }

//        item?.Id = message.MessageId;
//        return item;
//    }
//    internal RealtimeConversationSessionOptions? ToOpenAISessionResponseOptions(LiveConversationResponseOptions? responseOptions)
//    {
//        if(responseOptions == null)
//        {
//            return null;
//        }
        

//        var sessionResponseOptions = new RealtimeConversationSessionOptions()
//        {
//            AudioOptions = new RealtimeConversationSessionAudioOptions(),
//            ToolChoice = OpenAI.Realtime.RealtimeToolChoice.Auto//responseOptions.ToolMode != null ? MapRealtimeToolChoice(responseOptions.ToolMode) : null,
//        };
//        //if (responseOptions.Tools is not null)
//        //{
//        //    foreach (var tool in responseOptions.Tools)
//        //    {

//        //        if (ConvertToConversationTool(tool) is ConversationTool converted)
//        //        {
//        //            sessionResponseOptions.Tools.Add(converted);
//        //        }
//        //    }
//        //}
//        //else if(_options?.Tools is not null)
//        //{
//        //    foreach (var tool in _options.Tools)
//        //    {
//        //        if(ConvertToConversationTool(tool) is ConversationTool converted)
//        //        {
//        //            sessionResponseOptions.Tools.Add(converted);
//        //        }
//        //    }
//        //}

//        //if(responseOptions.Modalities is ConversationModalitySet modalitySet)
//        //{
//        //    sessionResponseOptions.ContentModalities = MapContentModalities(modalitySet);
//        //}

//        sessionResponseOptions.Instructions = responseOptions.Instructions ?? _options?.Instructions;

//        return sessionResponseOptions;
//    }

//    //internal static ConversationSessionOptions? ToOpenAISessionOptions(LiveConversationSessionOptions? options)
//    //{
//    //    if (options is null)
//    //    {
//    //        return null;
//    //    }

//    //    var sessionOptions = new ConversationSessionOptions()
//    //    {

//    //    };

//    //    if(options.Modalities is ConversationModalitySet modalitySet)
//    //    {
//    //        sessionOptions.ContentModalities = MapContentModalities(modalitySet);
//    //    }

//    //    if (!string.IsNullOrWhiteSpace(options.Voice))
//    //    {
//    //        sessionOptions.Voice = new ConversationVoice(options.Voice.ToLower());
//    //    }

//    //    if (options.Instructions != null)
//    //    {
//    //        sessionOptions.Instructions = options.Instructions;
//    //    }

//    //    if (options.InputAudioFormat != null)
//    //    {
//    //        sessionOptions.InputAudioFormat = MapAudioFormat(options.InputAudioFormat);
//    //    }

//    //    if (options.OutputAudioFormat != null)
//    //    {
//    //        sessionOptions.OutputAudioFormat = MapAudioFormat(options.OutputAudioFormat);
//    //    }

//    //    if (options.InputTranscription != null)
//    //    {
//    //        sessionOptions.InputTranscriptionOptions = new()
//    //        {
//    //            Model = options.InputTranscription.Model?.ToLower() switch
//    //            {
//    //                "whisper-1" => InputTranscriptionModel.Whisper1,
//    //                _ => InputTranscriptionModel.Whisper1
//    //            }
//    //        };
//    //    }

//    //    if (options.TurnDetection != null)
//    //    {
//    //        sessionOptions.TurnDetectionOptions = options.TurnDetection.Type switch
//    //        {
//    //            RealtimeTurnDetectionType.ServerVad => TurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
//    //                silenceDuration: TimeSpan.FromMilliseconds(options.TurnDetection.SilenceThresholdMs ?? 500),
//    //                detectionThreshold: options.TurnDetection.VadThreshold,
//    //                prefixPaddingDuration: TimeSpan.FromMilliseconds(options.TurnDetection.PrefixPaddingMs ?? 300),
//    //                enableAutomaticResponseCreation: options.TurnDetection.EnableAutomaticResponse,
//    //                enableResponseInterruption: options.TurnDetection.EnableResponseInterruption
//    //                ),
//    //            RealtimeTurnDetectionType.SemanticVad => TurnDetectionOptions.CreateSemanticVoiceActivityTurnDetectionOptions(SemanticEagernessLevel.Auto),
//    //            _ => TurnDetectionOptions.CreateDisabledTurnDetectionOptions()
//    //        };
//    //    }

//    //    if (options.Temperature.HasValue)
//    //    {
//    //        sessionOptions.Temperature = options.Temperature.Value;
//    //    }

//    //    if (options.MaxOutputTokens.HasValue)
//    //    {
//    //        sessionOptions.MaxOutputTokens = options.MaxOutputTokens.Value;
//    //    }

//    //    if (options.Tools != null)
//    //    {
//    //        foreach (var tool in options.Tools)
//    //        {
//    //            if(tool is AIFunctionDeclaration af)
//    //            {
//    //                sessionOptions.Tools.Add(ConvertToConversationTool(tool));
//    //            }
//    //        }
//    //    }

//    //    if (options.ToolMode != null)
//    //    {
//    //        sessionOptions.ToolChoice = MapRealtimeToolChoice(options.ToolMode);
//    //    }

//    //    return sessionOptions;
//    //}

//    //private static ConversationToolChoice MapRealtimeToolChoice(ChatToolMode toolChoice)
//    //{
//    //    return toolChoice switch
//    //    {
//    //        NoneChatToolMode => ConversationToolChoice.CreateNoneToolChoice(),
//    //        AutoChatToolMode => ConversationToolChoice.CreateAutoToolChoice(),
//    //        RequiredChatToolMode required => !string.IsNullOrEmpty(required.RequiredFunctionName)
//    //            ? ConversationToolChoice.CreateFunctionToolChoice(required.RequiredFunctionName)
//    //            : ConversationToolChoice.CreateRequiredToolChoice(),
//    //        _ => ConversationToolChoice.CreateAutoToolChoice()
//    //    };
//    //}

//    //private static RealtimeAudioFormat MapAudioFormat(ConversationAudioFormat format)
//    //{
//    //    return format.Encoding.ToLower() switch
//    //    {
//    //        "pcm16" => RealtimeAudioFormat.Pcm16,
//    //        "g711_ulaw" => RealtimeAudioFormat.G711Ulaw,
//    //        "g711_alaw" => RealtimeAudioFormat.G711Alaw,
//    //        _ => RealtimeAudioFormat.Pcm16
//    //    };
//    //}

//    //private static RealtimeContentModalities MapContentModalities(ConversationModalitySet conversationModalities)
//    //{
//    //    if (conversationModalities.IsEmpty) return RealtimeOutputModality.;

//    //    var hasAudio = conversationModalities.Contains(ConversationModality.Audio);
//    //    var hasText = conversationModalities.Contains(ConversationModality.Text);

//    //    return (hasAudio, hasText) switch
//    //    {
//    //        (true, false) => RealtimeOutputModality.Audio,
//    //        (false, true) => RealtimeOutputModality.Text,
//    //        _ => RealtimeContentModalities.Default
//    //    };
//    //}

//    private static RealtimeFunctionTool? ConvertToConversationTool(AITool tool)
//    {
//        if (tool is not AIFunctionDeclaration function)
//        {
//            return null;
//        }
//        return OpenAIClientExtensions.ToOpenAIRealtimeFunctionTool(function);
//    }

//    private void UpdateSessionState(RealtimeSessionState newState, ErrorContent? error = null, string? reason = null)
//    {
//        var oldState = _state;
//        _state = newState;

//        StateChanged?.Invoke(this, new RealtimeSessionStateChangedEventArgs
//        {
//            PreviousState = oldState,
//            NewState = newState,
//            Error = error,
//            Reason = reason
//        });
//    }

//    private void ThrowIfDisposed()
//    {
//        ObjectDisposedException.ThrowIf(_disposed, this);
//    }
//}
