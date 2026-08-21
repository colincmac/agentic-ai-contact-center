using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Azure.AI.VoiceLive;
using Extensions.AI.Contents;
using Extensions.AI.RealtimeVoice;
using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;
using OpenAI.Realtime;

namespace Extensions.AI.RealtimeVoice.AzureVoiceLive;



/// <summary>
/// The Session object, which controls the parameters of the interaction, like the model being used, the voice used to generate output, and other configuration.
/// A Conversation, which represents user input Items and model output Items generated during the current session.
/// Responses, which are model-generated audio or text Items that are added to the Conversation.
/// An OpenAI session has a max duration of 30minutes
/// </summary>
[Obsolete("Please use `AzureVoiceLiveClientSession` instead")]

public sealed class AzureVoiceLiveConversationSession : ILiveConversationSession
{

    private const string AzureDeepNoiseSuppressionValue = "azure_deep_noise_suppression";
    private const string NearFieldValue = "near_field";
    private const string FarFieldValue = "far_field";

    private VoiceLiveSession? _session;
    private readonly string _realtimeModelId;

    private readonly AzureVoiceLiveConversationClient _parentClient;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task<VoiceLiveSession>? _sessionInitializationTask;

    private LiveConversationSessionOptions? _options;
    private readonly TaskCompletionSource<string?> _sessionStartTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RealtimeSessionState _state = RealtimeSessionState.Connecting;

    private bool _disposed;

    public AzureVoiceLiveConversationSession(
        AzureVoiceLiveConversationClient parentClient,
        string realtimeModelId,
        LiveConversationSessionOptions? options = null,
        ILogger? logger = null)
    {
        _parentClient = Throw.IfNull(parentClient);
        _options = options;
        _state = RealtimeSessionState.None;

        _logger = logger ?? NullLogger.Instance;
        Metadata = new(realtimeModelId);
        _realtimeModelId = realtimeModelId;
    }

    internal AzureVoiceLiveConversationSession(
        AzureVoiceLiveConversationClient parentClient,
        VoiceLiveSession session,
        string realtimeModelId,
        LiveConversationSessionOptions? options = null,
        ILogger? logger = null)
    {
        _parentClient = Throw.IfNull(parentClient);
        _session = Throw.IfNull(session);
        _options = options;
        _state = RealtimeSessionState.Connecting;

        _logger = logger ?? NullLogger.Instance;
        Metadata = new(realtimeModelId);
        _realtimeModelId = realtimeModelId;

    }

    public LiveConversationSessionMetadata Metadata { get; }
    public LiveConversationSessionOptions? CurrentSessionConfiguration => _options;

    private string? _sessionId;
    /// <inheritdoc/>
    public string? SessionId => _sessionId;

    /// <inheritdoc/>
    public RealtimeSessionState State => _state;

    /// <inheritdoc/>
    public event EventHandler<RealtimeSessionStateChangedEventArgs>? StateChanged;

    public IList<AITool> SessionTools => _options?.Tools ?? [];

    public async Task StartResponseAsync(LiveConversationResponseOptions? responseOptions, CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        await session.StartResponseAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendAudioAsync(
        ReadOnlyMemory<byte> audioData,
        CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {

            await session.SendInputAudioAsync(BinaryData.FromBytes(audioData.ToArray()), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SendAudioStreamAsync(
        Stream audioStream,
        CancellationToken cancellationToken = default)
    {
       
        Throw.IfNull(audioStream);
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        await session.SendInputAudioAsync(
            audioStream, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendMessagesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var message in messages)
        {
            if (ToConversationRequestItem(message) is not { } realtimeItem)
            {
                continue;
            }
            await session.AddItemAsync(realtimeItem, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(LiveConversationResponseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var update in FromVoiceLiveSessionUpdatesAsync(session.GetUpdatesAsync(cancellationToken), cancellationToken))
        {
            if (update.RawRepresentation is SessionUpdateSessionCreated started)
            {
                _sessionId = started.Session.Id;
                _sessionStartTcs.TrySetResult(_sessionId);
                UpdateSessionState(RealtimeSessionState.Connected);
            }
            yield return update;
        }
    }


    internal static async IAsyncEnumerable<ChatResponseUpdate> FromVoiceLiveSessionUpdatesAsync(
        IAsyncEnumerable<SessionUpdate> streamingRealtimeUpdates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DateTimeOffset? createdAt = null;
        string? responseId = null;
        string? conversationId = null;
        string? sessionId = null;
        string? modelId = null;
        string? lastMessageId = null;
        ChatRole? lastRole = null;
        HashSet<string> unHandledFunctionCalls = [];

        // Local helper to construct updates with current accumulated state.
        ChatResponseUpdate CreateUpdate(List<AIContent>? contents = null)
        {
            var update = new ChatResponseUpdate(lastRole, contents)
            {
                ConversationId = conversationId,
                CreatedAt = createdAt,
                MessageId = lastMessageId,
                ModelId = modelId,
                RawRepresentation = null, // set below after switch when we have streamingUpdate
                ResponseId = responseId,
            };

            return update;
        }

        await foreach (var streamingUpdate in streamingRealtimeUpdates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (streamingUpdate)
            {
                case SessionUpdateSessionCreated sessionStarted:
                    {
                        lastRole = ChatRole.Assistant;
                        modelId = sessionStarted.Session.Model;
                        conversationId ??= sessionStarted.Session.Id;
                        sessionId ??= sessionStarted.Session.Id;
                        createdAt ??= DateTimeOffset.UtcNow;

                        var update = CreateUpdate();
                        update.RawRepresentation = sessionStarted;
                        yield return update;
                        break;
                    }

                case SessionUpdateSessionUpdated sessionConfigured:
                    {
                        modelId = sessionConfigured.Session.Model;
                        lastRole = ChatRole.Assistant;

                        var update = CreateUpdate();
                        update.RawRepresentation = sessionConfigured;
                        yield return update;
                        break;
                    }

                case SessionUpdateResponseCreated responseStarted:
                    {
                        responseId = responseStarted.Response.Id;
                        conversationId = responseStarted.Response.ConversationId;   
                        lastRole = ChatRole.Assistant;
                        var update = CreateUpdate();
                        update.Contents.Add(new RealtimeResponseStartContent(responseStarted.EventId));

                        update.RawRepresentation = responseStarted;

                        yield return update;
                        break;
                    }

                case SessionUpdateResponseAudioDelta audioDelta when !audioDelta.Delta.IsEmpty:
                    {
                        lastMessageId = audioDelta.ItemId;
                        responseId = audioDelta.ResponseId;
                        lastRole = ChatRole.Assistant;
                        var update = CreateUpdate([new DataContent(audioDelta.Delta.ToArray(), "audio/pcm")]);
                      
                        update.RawRepresentation = audioDelta;
                        yield return update;

                        break;
                    }

                case SessionUpdateResponseAudioTranscriptDelta transcriptDelta:
                    {
                        lastMessageId = transcriptDelta.ItemId;
                        responseId = transcriptDelta.ResponseId;
                        lastRole = ChatRole.Assistant;
                        var update = CreateUpdate([new AudioTranscriptionContent(transcriptDelta.Delta)]);

                        update.RawRepresentation = transcriptDelta;
                        yield return update;

                        break;
                    }

                case SessionUpdateResponseFunctionCallArgumentsDone functionCall:
                    {
                        lastMessageId = functionCall.ItemId;
                        responseId = functionCall.ResponseId;
                        lastRole = ChatRole.Assistant;
                        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(functionCall.Arguments);

                        var update = CreateUpdate([new FunctionCallContent(functionCall.CallId, functionCall.Name, parameters)]);
                        update.RawRepresentation = functionCall;
                        yield return update;
                        break;
                    }

                case SessionUpdateResponseAudioTranscriptDone textFinished:
                    {
                        lastMessageId = textFinished.ItemId;
                        responseId = textFinished.ResponseId;
                        lastRole = ChatRole.Assistant;
                        
                        var update = CreateUpdate([new TextContent(textFinished.Transcript)]);
                        update.RawRepresentation = textFinished;
                        yield return update;
                        break;
                    }


                case SessionUpdateResponseDone responseFinished:
                    {
                        responseId = responseFinished.Response.Id;
                        conversationId = responseFinished.Response.ConversationId;
                        lastRole = ChatRole.Assistant;
                        var update = CreateUpdate();

                        update.FinishReason = ChatFinishReason.Stop; //ToFinishReason(responseFinished.Response.Status?.IncompleteReason) ?? (unHandledFunctionCalls.Count != 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
                        update.Contents.Add(new UsageContent(ToUsageDetails(responseFinished.Response.Usage)));
                        update.Contents.Add(new RealtimeResponseFinishedContent(responseFinished.Response.Id));
                        update.RawRepresentation = responseFinished;

                        yield return update;
                        break;
                    }


                case SessionUpdateInputAudioBufferSpeechStarted speechStarted:
                    {
                        lastMessageId = speechStarted.ItemId;
                        lastRole = ChatRole.User;
                        var update = CreateUpdate([
                            new AudioTranscriptionContent(referenceItemId: speechStarted.ItemId) { StartTime = speechStarted.AudioStart },
                            new RealtimeVadContent(VadEventType.InputSpeechStarted) { StartTime = speechStarted.AudioStart }
                            ]);
                        update.RawRepresentation = speechStarted;
    
                        yield return update;
                        break;
                    }

                case SessionUpdateConversationItemInputAudioTranscriptionDelta inputAudioTxDelta:
                    {
                        if (!string.IsNullOrEmpty(inputAudioTxDelta.Delta))
                        {
                            lastMessageId = inputAudioTxDelta.ItemId;
                            lastRole = ChatRole.User;

                            var update = CreateUpdate([new AudioTranscriptionContent(text: inputAudioTxDelta.Delta, referenceItemId: inputAudioTxDelta.ItemId, referenceContentIndex: inputAudioTxDelta.ContentIndex)]);
                            update.RawRepresentation = inputAudioTxDelta;
                            yield return update;
                        }
                        break;
                    }

                case SessionUpdateConversationItemInputAudioTranscriptionCompleted inputAudioTxFinished:
                    {
                        if (!string.IsNullOrEmpty(inputAudioTxFinished.Transcript))
                        {
                            lastMessageId = inputAudioTxFinished.ItemId;
                            lastRole = ChatRole.User;

                            var update = CreateUpdate([new TextContent(inputAudioTxFinished.Transcript)]);
                            update.RawRepresentation = inputAudioTxFinished;
                            yield return update;
                        }
                        break;
                    }



                case SessionUpdateInputAudioBufferSpeechStopped speechFinished:
                    {
                        lastMessageId = speechFinished.ItemId;
                        lastRole = ChatRole.User;
                        var update = CreateUpdate([
                            new RealtimeVadContent(VadEventType.InputSpeechEnded)
                            {
                                EndTime = speechFinished.AudioEnd
                            }]);
                        update.RawRepresentation = speechFinished;
                        yield return update;
                        break;
                    }

                default:
                    {
                        // Future-proof: unknown subclass we didn’t explicitly handle.
                        var update = CreateUpdate();
                        update.RawRepresentation = streamingUpdate;
                        yield return update;
                        break;
                    }
            }
        }
    }


    private static UsageDetails ToUsageDetails(ResponseTokenStatistics usage) =>
        new()
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalTokens,
        };
    /// <inheritdoc/>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Interrupting response for session {SessionId}", SessionId);

        await session.CancelResponseAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CommitPendingAudioAsync(CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Committing input buffer for session {SessionId}", SessionId);

        await session.CommitInputAudioAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearInputAudioAsync(CancellationToken cancellationToken = default)
    {
       
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Clearing input buffer for session {SessionId}", SessionId);

        await session.ClearInputAudioAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ConfigureSessionAsync(
        LiveConversationSessionOptions options,
        CancellationToken cancellationToken = default)
    {
       
        Throw.IfNull(options);
        var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Configuring session {SessionId}", SessionId);

        var sessionOptions = ToVoiceLiveSessionOptions(options);
        await session.ConfigureSessionAsync(sessionOptions, cancellationToken)
            .ConfigureAwait(false);
        // Update local options
        _options = options;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        Throw.IfNull(serviceType);

        return serviceKey is null
            && (serviceType == typeof(ILiveConversationSession)
                || serviceType == typeof(AzureVoiceLiveConversationSession))
            ? this
            : serviceType == typeof(VoiceLiveSession)
            ? _session
            : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UpdateSessionState(RealtimeSessionState.Closing);

        try
        {
            _sendLock?.Dispose();
            _session?.Dispose();
            UpdateSessionState(RealtimeSessionState.Closed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing session {SessionId}", SessionId);
            UpdateSessionState(RealtimeSessionState.Error, new ErrorContent(ex.Message));
        }
    }

    private Task<VoiceLiveSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Fast path if already initialized.
        var existingTask = _sessionInitializationTask;
        if (existingTask is not null)
        {
            return existingTask;
        }

        // Slow path: create and attempt to publish.
        return InitializeSessionSlowAsync(cancellationToken);

        async Task<VoiceLiveSession> InitializeSessionSlowAsync(CancellationToken ct)
        {
            var newTask = CreateSessionAsync(ct);

            // Publish only if still null; otherwise use the winner.
            var winner = Interlocked.CompareExchange(ref _sessionInitializationTask, newTask, null);
            if (winner is not null)
            {
                return await winner.ConfigureAwait(false);
            }
            return await newTask.ConfigureAwait(false);
        }
    }

    private async Task<VoiceLiveSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        UpdateSessionState(RealtimeSessionState.Connecting);

        var realtimeClient = _parentClient.GetService<VoiceLiveClient>();
        if (realtimeClient is null)
        {
            throw new InvalidOperationException("Cannot create session because the parent client does not have a RealtimeClient service.");
        }

        var session = await realtimeClient.StartSessionAsync(_realtimeModelId, cancellationToken: cancellationToken).ConfigureAwait(false);
        _session = session;

        var initialOptions = ToVoiceLiveSessionOptions(_options);
        if (initialOptions is not null)
        {
            await session.ConfigureSessionAsync(initialOptions, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }


    private static ConversationRequestItem? ToConversationRequestItem(ChatMessage message)
    {
        if (message.RawRepresentation is ConversationRequestItem rawItem)
        {
            return rawItem;
        }

        ConversationRequestItem? item;

        // TODO: Do we need to handle FunctionCallContent here as well?
        if (message.Role == ChatRole.Tool
            && message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } resultContent)
        {
            string? result = resultContent.Result as string;
            if (result is null && resultContent.Result is not null)
            {
                try
                {
                    result = JsonSerializer.Serialize(resultContent.Result, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
                }
                catch (NotSupportedException)
                {
                    // If the type can't be serialized, skip it.
                }
            }

            item = new FunctionCallOutputItem(resultContent.CallId, result);
        }
        else
        {
            //var contents = message.Contents
            //    .Select(c => ToConversationContentPart(c, message.Role))
            //    .Where(c => c != null);
            item = message switch
            {
                { Role: var role } when role == ChatRole.User => new UserMessageItem(message.Text),
                { Role: var role } when role == ChatRole.Assistant => new AssistantMessageItem(message.Text),
                { Role: var role } when role == ChatRole.System => new SystemMessageItem(message.Text),
                _ => new AssistantMessageItem(message.Text)
            };
        }

        item?.Id = message.MessageId;
        return item;
    }

    /// <summary>
    /// See https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to#session-configuration
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    internal static VoiceLiveSessionOptions? ToVoiceLiveSessionOptions(LiveConversationSessionOptions? options)
    {

        if (options is null)
        {
            return null;
        }

        VoiceLiveSessionOptions? sessionOptions = null;
        if(options.RawSessionOptionsJson is string json)
        {
            var data = BinaryData.FromString(json);

            // Uses the generated deserializer you showed (format "J")
            sessionOptions = ModelReaderWriter.Read<VoiceLiveSessionOptions>(
                data,
                new ModelReaderWriterOptions("J"),
                AzureAIVoiceLiveContext.Default);
        }

        sessionOptions ??= new VoiceLiveSessionOptions()
        {
            ToolChoice = options.ToolMode is not null ? MapRealtimeToolChoice(options.ToolMode) : ToolChoiceLiteral.Auto,
            InputAudioFormat = options.InputAudioFormat is not null ? MapAudioFormat(options.InputAudioFormat) : InputAudioFormat.Pcm16,
            Instructions = options.Instructions
            
        };
        if (options.VoiceOptions is ConversationVoiceOptions voiceOptions)
        {
            sessionOptions.Voice = MapVoiceProvider(voiceOptions);
        }

        if (options.Tools != null)
        {
            foreach (var tool in options.Tools)
            {
                if(tool is AIFunctionDeclaration af)
                {
                    sessionOptions.Tools.Add(ConvertToConversationTool(af));
                }
            }
        }
        if (options.Modalities is ConversationModalitySet modalitySet)
        {
            foreach(var mod in MapContentModalities(modalitySet))
            {
                sessionOptions.Modalities.Add(mod);
            }
        }
        if (options.Temperature.HasValue)
        {
            sessionOptions.Temperature = options.Temperature.Value;
        }

        if (options.MaxOutputTokens.HasValue)
        {
            sessionOptions.MaxResponseOutputTokens = options.MaxOutputTokens.Value;
        }

        if(options.TurnDetection is Configuration.RealtimeTurnDetection turnDection)
        {
            sessionOptions.TurnDetection = MapTurnDetectionOption(turnDection);
        }

        if (options.Temperature.HasValue)
        {
            sessionOptions.Temperature = options.Temperature.Value;
        }

        if (!string.IsNullOrEmpty(options.InputNoiceReductionType))
        {
            sessionOptions.InputAudioNoiseReduction = new AudioNoiseReduction(MapNoiseReduction(options.InputNoiceReductionType));
        }

        return sessionOptions;
    }

    /// <summary>
    /// https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to#turn-detection-parameters
    /// </summary>
    /// <param name="turnDetection"></param>
    /// <returns></returns>
    private static TurnDetection MapTurnDetectionOption(Configuration.RealtimeTurnDetection turnDetection)
    {
        var defaultprefixPadding = TimeSpan.FromMilliseconds(300);
        var defaultSilenceDuration = TimeSpan.FromMilliseconds(500);
        var defaultSpeechDuration = TimeSpan.FromMilliseconds(80);
        var defaultVadThreshold = 0.3f;

        var defaultDetection = new AzureSemanticVadTurnDetection()
        {
            AutoTruncate = turnDetection.EnableAutomaticTruncation,
            CreateResponse = turnDetection.EnableAutomaticResponse,
            InterruptResponse = turnDetection.EnableResponseInterruption,
            PrefixPadding = turnDetection.PrefixPaddingMs is null ? defaultprefixPadding : TimeSpan.FromMilliseconds((int)turnDetection.PrefixPaddingMs),
            SilenceDuration = turnDetection.SilenceThresholdMs is null ? defaultSilenceDuration : TimeSpan.FromMilliseconds((int)turnDetection.SilenceThresholdMs),
            Threshold = turnDetection.VadThreshold ?? defaultVadThreshold,
            RemoveFillerWords = true,
            SpeechDuration = turnDetection.SpeechDurationMs is null ? defaultSpeechDuration : TimeSpan.FromMilliseconds((int)turnDetection.SpeechDurationMs)
        };
        var defaultEouDetection = new AzureSemanticEouDetectionEn()
        {
            ThresholdLevel = EouThresholdLevel.Medium,
            Timeout = TimeSpan.FromSeconds(2)
        };
        return turnDetection.Type switch
        {
            RealtimeTurnDetectionType.Disabled => new NoTurnDetection(),
            RealtimeTurnDetectionType.AzureSemanticVadMultiLingual => new AzureSemanticVadTurnDetectionMultilingual()
            {
                AutoTruncate = turnDetection.EnableAutomaticTruncation,
                CreateResponse = turnDetection.EnableAutomaticResponse,
                InterruptResponse = turnDetection.EnableResponseInterruption,
                PrefixPadding = turnDetection.PrefixPaddingMs is null ? defaultprefixPadding : TimeSpan.FromMilliseconds((int)turnDetection.PrefixPaddingMs),
                SilenceDuration = turnDetection.SilenceThresholdMs is null ? defaultSilenceDuration : TimeSpan.FromMilliseconds((int)turnDetection.SilenceThresholdMs),
                Threshold = turnDetection.VadThreshold ?? defaultVadThreshold,
                RemoveFillerWords = true,
                SpeechDuration = defaultSpeechDuration
            },
            RealtimeTurnDetectionType.ServerVad => new ServerVadTurnDetection()
            {
                AutoTruncate = turnDetection.EnableAutomaticTruncation,
                CreateResponse = turnDetection.EnableAutomaticResponse,
                InterruptResponse = turnDetection.EnableResponseInterruption,
                PrefixPadding = turnDetection.PrefixPaddingMs is null ? defaultprefixPadding : TimeSpan.FromMilliseconds((int)turnDetection.PrefixPaddingMs),
                SilenceDuration = turnDetection.SilenceThresholdMs is null ? defaultSilenceDuration : TimeSpan.FromMilliseconds((int)turnDetection.SilenceThresholdMs),
                Threshold = turnDetection.VadThreshold ?? defaultVadThreshold
            },
            RealtimeTurnDetectionType.AzureSemanticVadEN => defaultDetection,
            RealtimeTurnDetectionType.SemanticVad => defaultDetection,
            _ => defaultDetection
        };
    }
    private static AudioNoiseReductionType MapNoiseReduction(string noiseReduction)
    {

        return noiseReduction.ToLower() switch
        {
            AzureDeepNoiseSuppressionValue => AudioNoiseReductionType.AzureDeepNoiseSuppression,
            NearFieldValue => AudioNoiseReductionType.NearField,
            FarFieldValue => AudioNoiseReductionType.FarField,
            _ => AudioNoiseReductionType.AzureDeepNoiseSuppression
        };
    }
    private static VoiceProvider MapVoiceProvider(ConversationVoiceOptions voice)
    {
        return voice switch
        {
           { Provider: ConversationVoiceProvider.Azure } => new AzureStandardVoice(voice.Name) { Temperature = voice.Temperature },
            _ => new OpenAIVoice(voice.Name)
        };
    }
    private static ToolChoiceOption MapRealtimeToolChoice(ChatToolMode toolChoice)
    {
        return toolChoice switch
        {
            NoneChatToolMode => ToolChoiceLiteral.None,
            AutoChatToolMode => ToolChoiceLiteral.Auto,
            RequiredChatToolMode required => !string.IsNullOrEmpty(required.RequiredFunctionName)
                ? new ToolChoiceOption(required.RequiredFunctionName)
                : ToolChoiceLiteral.Required,
            _ => ToolChoiceLiteral.Auto
        };
    }

    private static InputAudioFormat MapAudioFormat(ConversationAudioFormat format)
    {
        return format.Encoding.ToLower() switch
        {
            "pcm16" => InputAudioFormat.Pcm16,
            "g711_ulaw" => InputAudioFormat.G711Ulaw,
            "g711_alaw" => InputAudioFormat.G711Alaw,
            _ => InputAudioFormat.Pcm16
        };
    }
    private static IList<InteractionModality> MapContentModalities(ConversationModalitySet conversationModalities)
    {
        if (conversationModalities.IsEmpty) return [];

        var hasAudio = conversationModalities.Contains(ConversationModality.Audio);
        var hasText = conversationModalities.Contains(ConversationModality.Text);

        return (hasAudio, hasText) switch
        {
            (true, false) => [InteractionModality.Audio],
            (false, true) => [InteractionModality.Text],
            _ => [InteractionModality.Text, InteractionModality.Audio]
        };
    }

    private static VoiceLiveFunctionDefinition? ConvertToConversationTool(AITool tool)
    {
        if (tool is not AIFunctionDeclaration function)
        {
            return null;
        }
        var definition = OpenAIClientExtensions.ToOpenAIRealtimeFunctionTool(function);
        return new VoiceLiveFunctionDefinition(function.Name) { Description = definition.FunctionDescription, Parameters = definition.FunctionParameters };
    }

    private void UpdateSessionState(RealtimeSessionState newState, ErrorContent? error = null, string? reason = null)
    {
        var oldState = _state;
        _state = newState;

        StateChanged?.Invoke(this, new RealtimeSessionStateChangedEventArgs
        {
            PreviousState = oldState,
            NewState = newState,
            Error = error,
            Reason = reason
        });
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
