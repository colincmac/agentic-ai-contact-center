// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.VoiceLive;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable OPENAI002 // OpenAI Realtime API is experimental


namespace Extensions.AI.Realtime.AzureVoiceLive;

/// <summary>Represents an <see cref="IRealtimeClientSession"/> for the Azure Voice Live API over WebSocket.</summary>
public sealed class AzureVoiceLiveClientSession : IRealtimeClientSession
{
    private readonly SessionTarget _sessionTarget;

    /// <summary>Metadata about this session's provider and model, used for OpenTelemetry.</summary>
    private readonly ChatClientMetadata _metadata;

    private readonly VoiceLiveSession _sessionClient;

    /// <summary>Whether the session has been disposed (0 = false, 1 = true).</summary>
    private int _disposed;

    /// <inheritdoc />
    public RealtimeSessionOptions? Options { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="AzureVoiceLiveClientSession"/> class from an already-connected session client.</summary>
    /// <param name="sessionClient">The connected SDK session client.</param>
    /// <param name="sessionTarget">The model target for metadata.</param>
    /// <param name="initialOptions">
    /// Optional initial <see cref="RealtimeSessionOptions"/> that were supplied to
    /// <see cref="VoiceLiveClient.StartSessionAsync(SessionTarget, VoiceLiveSessionOptions, CancellationToken)"/>.
    /// Used to seed <see cref="Options"/> so the property reflects the effective configuration
    /// before any <c>session.created</c> / <c>session.updated</c> server event arrives.
    /// </param>
    internal AzureVoiceLiveClientSession(
        VoiceLiveSession sessionClient,
        SessionTarget sessionTarget,
        RealtimeSessionOptions? initialOptions = null)
    {
        _sessionClient = Throw.IfNull(sessionClient);
        _sessionTarget = Throw.IfNull(sessionTarget);
        _metadata = new("azure_voice_live", defaultModelId: _sessionTarget.ToString());
        Options = initialOptions;
    }

    private async Task UpdateSessionAsync(RealtimeSessionOptions options, CancellationToken cancellationToken)
    {
        // Voice Live session.update is a PATCH: only the fields that are present are updated.
        // Merge the incoming partial update into the current effective state so we never
        // accidentally clear fields the caller didn't explicitly set on this update.
        var effective = MergeOptions(Options, options);

        // The model cannot be changed after the session has been initialized, so omit it
        // from session.update payloads even if the merged state still carries the original value.
        var sessionOptions = BuildSessionOptions(
            effective,
            TryGetRawSessionOptions(effective.RawRepresentationFactory?.Invoke()),
            includeModel: false);

        await _sessionClient.ConfigureSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);

        // Optimistically reflect the merged state. The server's session.updated response
        // (handled in HandleSessionEvent) will reconcile this with the authoritative view.
        Options = effective;
    }


    /// <inheritdoc />
    public async Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(message);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            switch (message)
            {
                case SessionUpdateRealtimeClientMessage sessionUpdate:
                    await UpdateSessionAsync(sessionUpdate.Options, cancellationToken).ConfigureAwait(false);
                    break;

                case CreateResponseRealtimeClientMessage responseCreate:
                    await SendResponseCreateAsync(responseCreate, cancellationToken).ConfigureAwait(false);
                    break;

                case CreateConversationItemRealtimeClientMessage itemCreate:
                    await SendConversationItemCreateAsync(itemCreate, cancellationToken).ConfigureAwait(false);
                    break;

                case InputAudioBufferAppendRealtimeClientMessage audioAppend:
                    await SendInputAudioAppendAsync(audioAppend, cancellationToken).ConfigureAwait(false);
                    break;

                case InputAudioBufferCommitRealtimeClientMessage:
                    await SendInputAudioCommitAsync(message, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await SendRawCommandAsync(message, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // Expected during session teardown or cancellation.
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _sessionClient.GetUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (MapServerUpdate(update) is { } serverMessage)
            {
                yield return serverMessage;
            }
        }
    }

    /// <inheritdoc />
    object? IRealtimeClientSession.GetService(Type serviceType, object? serviceKey)
    {
        _ = Throw.IfNull(serviceType);

        return
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            serviceType.IsInstanceOfType(this) ? this :
            serviceType.IsInstanceOfType(_sessionClient) ? _sessionClient :
            null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return default;
        }

        _sessionClient.Dispose();
        return default;
    }

    #region Send Helpers

    private async Task SendResponseCreateAsync(CreateResponseRealtimeClientMessage responseCreate, CancellationToken cancellationToken)
    {
        if (TryGetRawJsonPayload(responseCreate.RawRepresentation) is { } rawPayload)
        {
            await _sessionClient.SendCommandAsync(rawPayload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (responseCreate.Items is not null)
        {
            foreach (var item in responseCreate.Items)
            {
                if (ToConversationRequestItem(item) is { } sdkItem)
                {
                    await _sessionClient.AddItemAsync(sdkItem, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var responseOptions = BuildResponseOptions(responseCreate);

        if (responseOptions is null)
        {
            await _sessionClient.StartResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionClient.StartResponseAsync(responseOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendConversationItemCreateAsync(CreateConversationItemRealtimeClientMessage itemCreate, CancellationToken cancellationToken)
    {
        if (TryGetRawJsonPayload(itemCreate.RawRepresentation) is { } rawPayload)
        {
            await _sessionClient.SendCommandAsync(rawPayload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (itemCreate.RawRepresentation is ConversationRequestItem rawItem)
        {
            await _sessionClient.AddItemAsync(rawItem, cancellationToken).ConfigureAwait(false);

            return;
        }

        var sdkItem = ToConversationRequestItem(itemCreate.Item);
        if (sdkItem is null)
        {
            return;
        }

        await _sessionClient.AddItemAsync(sdkItem, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInputAudioAppendAsync(InputAudioBufferAppendRealtimeClientMessage audioAppend, CancellationToken cancellationToken)
    {
        if (audioAppend.Content is null || !audioAppend.Content.HasTopLevelMediaType("audio"))
        {
            return;
        }

        var audioBytes = ExtractAudioBinaryData(audioAppend.Content).ToArray();

        await _sessionClient.SendInputAudioAsync(audioBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInputAudioCommitAsync(RealtimeClientMessage _, CancellationToken cancellationToken)
    {
        await _sessionClient.CommitInputAudioAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRawCommandAsync(RealtimeClientMessage message, CancellationToken cancellationToken)
    {
        if (TryGetRawJsonPayload(message.RawRepresentation) is { } rawPayload)
        {
            if (message.MessageId is not null)
            {
                string jsonString = rawPayload.ToString();
                if (!jsonString.Contains("\"event_id\"", StringComparison.Ordinal))
                {
                    jsonString = jsonString.Insert(1, $"\"event_id\":{JsonSerializer.Serialize(message.MessageId, OpenAIRealtimeJsonContext.Default.String)},");
                    rawPayload = BinaryData.FromString(jsonString);
                }
            }

            await _sessionClient.SendCommandAsync(rawPayload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merges a partial <see cref="RealtimeSessionOptions"/> update into the current
    /// effective options, preserving any field on <paramref name="current"/> that the
    /// caller did not explicitly set on <paramref name="update"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors Voice Live's <c>session.update</c> patch semantics: "Only the fields
    /// that are present are updated." Treats <see langword="null"/> on <paramref name="update"/>
    /// as "not provided" and falls back to <paramref name="current"/> for that property.
    /// </remarks>
    internal static RealtimeSessionOptions MergeOptions(RealtimeSessionOptions? current, RealtimeSessionOptions update)
    {
        _ = Throw.IfNull(update);

        if (current is null)
        {
            return update;
        }

        return new RealtimeSessionOptions
        {
            SessionKind = update.SessionKind != default ? update.SessionKind : current.SessionKind,
            Model = update.Model ?? current.Model,
            Instructions = update.Instructions ?? current.Instructions,
            InputAudioFormat = update.InputAudioFormat ?? current.InputAudioFormat,
            TranscriptionOptions = update.TranscriptionOptions ?? current.TranscriptionOptions,
            OutputAudioFormat = update.OutputAudioFormat ?? current.OutputAudioFormat,
            Voice = update.Voice ?? current.Voice,
            MaxOutputTokens = update.MaxOutputTokens ?? current.MaxOutputTokens,
            OutputModalities = update.OutputModalities ?? current.OutputModalities,
            ToolMode = update.ToolMode ?? current.ToolMode,
            Tools = update.Tools ?? current.Tools,
            VoiceActivityDetection = update.VoiceActivityDetection ?? current.VoiceActivityDetection,
            RawRepresentationFactory = update.RawRepresentationFactory ?? current.RawRepresentationFactory,
        };
    }

    /// <summary>
    /// Builds the <see cref="VoiceLiveSessionOptions"/> sent on the initial
    /// <see cref="VoiceLiveClient.StartSessionAsync(SessionTarget, VoiceLiveSessionOptions, CancellationToken)"/>
    /// handshake. Unlike <see cref="UpdateSessionAsync"/>, this path is allowed to set
    /// the model because the session has not yet been initialized.
    /// </summary>
    /// <param name="options">The caller-supplied realtime options.</param>
    /// <param name="sessionTarget">The session target; used to fill in a default model when the caller did not specify one.</param>
    internal static VoiceLiveSessionOptions BuildInitialSessionOptions(RealtimeSessionOptions options, SessionTarget sessionTarget)
    {
        _ = Throw.IfNull(options);
        _ = Throw.IfNull(sessionTarget);

        var seed = TryGetRawSessionOptions(options.RawRepresentationFactory?.Invoke());
        var sessionOptions = BuildSessionOptions(options, seed, includeModel: true);

        // Ensure the initial handshake always carries a model so Voice Live can pick
        // the right backend. Falls back to the target's model when the caller did not
        // explicitly set one.
        if (string.IsNullOrEmpty(sessionOptions.Model) && !string.IsNullOrEmpty(sessionTarget.Model))
        {
            sessionOptions.Model = sessionTarget.Model;
        }

        return sessionOptions;
    }

    private static VoiceLiveSessionOptions BuildSessionOptions(
        RealtimeSessionOptions options,
        VoiceLiveSessionOptions? seedOptions = null,
        bool includeModel = true)
    {
        var sessionOptions = seedOptions ?? new VoiceLiveSessionOptions();

        if (includeModel && options.Model is not null)
        {
            sessionOptions.Model = options.Model;
        }

        if (options.Instructions is not null)
        {
            sessionOptions.Instructions = options.Instructions;
        }

        if (options.InputAudioFormat is not null && ToVoiceLiveInputAudioFormat(options.InputAudioFormat) is { } inputAudioFormat)
        {
            sessionOptions.InputAudioFormat = inputAudioFormat;
        }

        if (options.TranscriptionOptions is not null)
        {
            sessionOptions.InputAudioTranscription = new AudioInputTranscriptionOptions(ToVoiceLiveTranscriptionModel(options.TranscriptionOptions.ModelId))
            {
                Language = options.TranscriptionOptions.SpeechLanguage,
            };
        }

        if (options.OutputAudioFormat is not null && ToVoiceLiveOutputAudioFormat(options.OutputAudioFormat) is { } outputAudioFormat)
        {
            sessionOptions.OutputAudioFormat = outputAudioFormat;
        }

        if (options.Voice is not null)
        {
            sessionOptions.Voice = ToAzureVoiceProvider(options.Voice);
        }

        if (options.MaxOutputTokens.HasValue)
        {
            sessionOptions.MaxResponseOutputTokens = options.MaxOutputTokens.Value;
        }

        if (options.OutputModalities is not null)
        {
            sessionOptions.Modalities.Clear();
            foreach (var modality in options.OutputModalities)
            {
                if (ToVoiceLiveInteractionModality(modality) is { } sdkModality)
                {
                    sessionOptions.Modalities.Add(sdkModality);
                }
            }
        }

        if (options.ToolMode is { } toolMode)
        {
            sessionOptions.ToolChoice = ToVoiceLiveToolChoice(toolMode);
        }

        if (options.Tools is not null)
        {
            sessionOptions.Tools.Clear();
            foreach (var tool in options.Tools)
            {
                if (ToVoiceLiveToolDefinition(tool) is { } sdkTool)
                {
                    sessionOptions.Tools.Add(sdkTool);
                }
            }
        }

        return sessionOptions;
    }

    private static VoiceLiveSessionOptions? BuildResponseOptions(CreateResponseRealtimeClientMessage responseCreate)
    {
        bool hasOverrides = false;
        var responseOptions = new VoiceLiveSessionOptions();

        if (responseCreate.Instructions is not null)
        {
            responseOptions.Instructions = responseCreate.Instructions;
            hasOverrides = true;
        }

        if (responseCreate.OutputVoice is not null)
        {
            responseOptions.Voice = ToAzureVoiceProvider(responseCreate.OutputVoice);
            hasOverrides = true;
        }

        if (responseCreate.OutputAudioOptions is not null && ToVoiceLiveOutputAudioFormat(responseCreate.OutputAudioOptions) is { } outputAudioFormat)
        {
            responseOptions.OutputAudioFormat = outputAudioFormat;
            hasOverrides = true;
        }

        if (responseCreate.MaxOutputTokens.HasValue)
        {
            responseOptions.MaxResponseOutputTokens = responseCreate.MaxOutputTokens.Value;
            hasOverrides = true;
        }

        if (responseCreate.OutputModalities is not null)
        {
            responseOptions.Modalities.Clear();
            foreach (var modality in responseCreate.OutputModalities)
            {
                if (ToVoiceLiveInteractionModality(modality) is { } sdkModality)
                {
                    responseOptions.Modalities.Add(sdkModality);
                }
            }

            hasOverrides = true;
        }

        if (responseCreate.ToolMode is { } toolMode)
        {
            responseOptions.ToolChoice = ToVoiceLiveToolChoice(toolMode);
            hasOverrides = true;
        }

        if (responseCreate.Tools is not null)
        {
            responseOptions.Tools.Clear();
            foreach (var tool in responseCreate.Tools)
            {
                if (ToVoiceLiveToolDefinition(tool) is { } sdkTool)
                {
                    responseOptions.Tools.Add(sdkTool);
                }
            }

            hasOverrides = true;
        }

        return hasOverrides ? responseOptions : null;
    }

    private static VoiceLiveSessionOptions? TryGetRawSessionOptions(object? rawOptions)
    {
        if (rawOptions is null)
        {
            return null;
        }

        if (rawOptions is VoiceLiveSessionOptions sessionOptions)
        {
            return sessionOptions;
        }

        if (rawOptions is string json)
        {
            return ModelReaderWriter.Read<VoiceLiveSessionOptions>(
                BinaryData.FromString(json),
                new ModelReaderWriterOptions("J"),
                AzureAIVoiceLiveContext.Default);
        }

        return null;
    }

    private static BinaryData? TryGetRawJsonPayload(object? rawRepresentation) => rawRepresentation switch
    {
        BinaryData data => data,
        string json => BinaryData.FromString(json),
        JsonObject jsonObject => BinaryData.FromString(jsonObject.ToJsonString()),
        _ => null,
    };

    private static ConversationRequestItem? ToConversationRequestItem(RealtimeConversationItem? contentItem)
    {
        if (contentItem is null)
        {
            return null;
        }

        if (contentItem.RawRepresentation is ConversationRequestItem rawItem)
        {
            return rawItem;
        }

        if (contentItem.Contents is not { Count: > 0 })
        {
            return null;
        }

        var firstContent = contentItem.Contents[0];
        ConversationRequestItem? item;

        if (firstContent is FunctionResultContent functionResult)
        {
            string resultJson = functionResult.Result as string ??
                (functionResult.Result is not null
                    ? JsonSerializer.Serialize(functionResult.Result, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)))
                    : string.Empty);

            item = new FunctionCallOutputItem(functionResult.CallId ?? string.Empty, resultJson);
        }
        else if (firstContent is FunctionCallContent functionCall)
        {
            string argumentsJson = functionCall.Arguments is not null
                ? JsonSerializer.Serialize(functionCall.Arguments, OpenAIRealtimeJsonContext.Default.IDictionaryStringObject)
                : "{}";

            item = new FunctionCallItem(functionCall.CallId ?? string.Empty, functionCall.Name, argumentsJson);
        }
        else
        {
            var contentParts = new List<MessageContentPart>();
            foreach (var content in contentItem.Contents)
            {
                if (content is TextContent textContent)
                {
                    contentParts.Add(new InputTextContentPart(textContent.Text ?? string.Empty));
                }
                else if (content is DataContent dataContent)
                {
                    if (dataContent.MediaType?.StartsWith("audio/", StringComparison.Ordinal) == true)
                    {
                        contentParts.Add(new InputAudioContentPart(Convert.ToBase64String(ExtractAudioBinaryData(dataContent).ToArray())));
                    }
                }
            }

            if (contentParts.Count == 0)
            {
                return null;
            }

            item = contentItem.Role switch
            {
                { Value: var role } when role == ChatRole.Assistant.Value => new AssistantMessageItem(contentParts),
                { Value: var role } when role == ChatRole.System.Value => new SystemMessageItem(contentParts),
                _ => new UserMessageItem(contentParts),
            };
        }

        if (item is not null && contentItem.Id is not null)
        {
            item.Id = contentItem.Id;
        }

        return item;
    }

    private static ToolChoiceOption ToVoiceLiveToolChoice(ChatToolMode toolMode) => toolMode switch
    {
        RequiredChatToolMode required when required.RequiredFunctionName is not null => new ToolChoiceOption(required.RequiredFunctionName),
        RequiredChatToolMode => ToolChoiceLiteral.Required,
        NoneChatToolMode => ToolChoiceLiteral.None,
        _ => ToolChoiceLiteral.Auto,
    };

    private static InputAudioFormat? ToVoiceLiveInputAudioFormat(RealtimeAudioFormat? format) => format?.MediaType switch
    {
        "audio/pcm" => InputAudioFormat.Pcm16,
        "audio/pcmu" => InputAudioFormat.G711Ulaw,
        "audio/pcma" => InputAudioFormat.G711Alaw,
        _ => null,
    };

    private static OutputAudioFormat? ToVoiceLiveOutputAudioFormat(RealtimeAudioFormat? format) => format?.MediaType switch
    {
        "audio/pcm" => OutputAudioFormat.Pcm16,
        "audio/pcmu" => OutputAudioFormat.G711Ulaw,
        "audio/pcma" => OutputAudioFormat.G711Alaw,
        _ => null,
    };

    private static InteractionModality? ToVoiceLiveInteractionModality(string? modality) => modality?.ToLowerInvariant() switch
    {
        "audio" => InteractionModality.Audio,
        "text" => InteractionModality.Text,
        _ => null,
    };

    private static VoiceProvider ToAzureVoiceProvider(string voice) => voice.ToLowerInvariant() switch
    {
        "alloy" or "ash" or "ballad" or "coral" or "echo" or "sage" or "shimmer" or "verse" => new OpenAIVoice(voice),
        _ => new AzureStandardVoice(voice),
    };

    private static VoiceLiveToolDefinition? ToVoiceLiveToolDefinition(AITool tool)
    {
        if (tool is AIFunctionDeclaration aiFunction)
        {
            var realtimeTool = OpenAIClientExtensions.ToOpenAIRealtimeFunctionTool(aiFunction);
            return new VoiceLiveFunctionDefinition(aiFunction.Name)
            {
                Description = realtimeTool.FunctionDescription,
                Parameters = realtimeTool.FunctionParameters,
            };
        }

        if (tool is HostedMcpServerTool mcpTool)
        {
            var definition = new VoiceLiveMcpServerDefinition(mcpTool.ServerName, mcpTool.ServerAddress);

            if (mcpTool.Headers is { Count: > 0 })
            {
                foreach (var kvp in mcpTool.Headers)
                {
                    definition.Headers.Add(kvp.Key, kvp.Value);
                }
            }

            if (mcpTool.AllowedTools is { Count: > 0 })
            {
                foreach (var toolName in mcpTool.AllowedTools)
                {
                    definition.AllowedTools.Add(toolName);
                }
            }

            return definition;
        }

        return null;
    }

    private static BinaryData ExtractAudioBinaryData(DataContent content)
    {
        string dataUri = content.Uri?.ToString() ?? string.Empty;
        int commaIndex = dataUri.LastIndexOf(',');

        if (commaIndex >= 0 && commaIndex < dataUri.Length - 1)
        {
            string base64 = dataUri[(commaIndex + 1)..];
            return BinaryData.FromBytes(Convert.FromBase64String(base64));
        }

        return BinaryData.FromBytes(content.Data.ToArray());
    }

    #endregion

    #region Receive Helpers (SDK → MEAI)

    private RealtimeServerMessage? MapServerUpdate(SessionUpdate update) => update switch
    {
        SessionUpdateError error => MapError(error),
        SessionUpdateSessionCreated created => HandleSessionEvent(created.Session, created),
        SessionUpdateSessionUpdated updated => HandleSessionEvent(updated.Session, updated),
        SessionUpdateResponseCreated created => MapResponseCreatedOrDone(created.EventId, created.Response, RealtimeServerMessageType.ResponseCreated, created),
        SessionUpdateResponseDone done => MapResponseCreatedOrDone(done.EventId, done.Response, RealtimeServerMessageType.ResponseDone, done),
        SessionUpdateResponseOutputItemAdded added => MapResponseOutputItem(added.EventId, added.ResponseId, added.OutputIndex, added.Item, RealtimeServerMessageType.ResponseOutputItemAdded, added),
        SessionUpdateResponseOutputItemDone done => MapResponseOutputItem(done.EventId, done.ResponseId, done.OutputIndex, done.Item, RealtimeServerMessageType.ResponseOutputItemDone, done),
        SessionUpdateResponseTextDelta textDelta => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputTextDelta)
        {
            MessageId = textDelta.EventId,
            ResponseId = textDelta.ResponseId,
            ItemId = textDelta.ItemId,
            OutputIndex = textDelta.OutputIndex,
            ContentIndex = textDelta.ContentIndex,
            Text = textDelta.Delta,
            RawRepresentation = textDelta,
        },
        SessionUpdateResponseTextDone textDone => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputTextDone)
        {
            MessageId = textDone.EventId,
            ResponseId = textDone.ResponseId,
            ItemId = textDone.ItemId,
            OutputIndex = textDone.OutputIndex,
            ContentIndex = textDone.ContentIndex,
            Text = textDone.Text,
            RawRepresentation = textDone,
        },
        SessionUpdateResponseAudioDelta audioDelta => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
        {
            MessageId = audioDelta.EventId,
            ResponseId = audioDelta.ResponseId,
            ItemId = audioDelta.ItemId,
            OutputIndex = audioDelta.OutputIndex,
            ContentIndex = audioDelta.ContentIndex,
            Audio = audioDelta.Delta is not null ? Convert.ToBase64String(audioDelta.Delta.ToArray()) : null,
            RawRepresentation = audioDelta,
        },
        SessionUpdateResponseAudioDone audioDone => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDone)
        {
            MessageId = audioDone.EventId,
            ResponseId = audioDone.ResponseId,
            ItemId = audioDone.ItemId,
            OutputIndex = audioDone.OutputIndex,
            ContentIndex = audioDone.ContentIndex,
            RawRepresentation = audioDone,
        },
        SessionUpdateResponseAudioTranscriptDelta transcriptDelta => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioTranscriptionDelta)
        {
            MessageId = transcriptDelta.EventId,
            ResponseId = transcriptDelta.ResponseId,
            ItemId = transcriptDelta.ItemId,
            OutputIndex = transcriptDelta.OutputIndex,
            ContentIndex = transcriptDelta.ContentIndex,
            Text = transcriptDelta.Delta,
            RawRepresentation = transcriptDelta,
        },
        SessionUpdateResponseAudioTranscriptDone transcriptDone => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioTranscriptionDone)
        {
            MessageId = transcriptDone.EventId,
            ResponseId = transcriptDone.ResponseId,
            ItemId = transcriptDone.ItemId,
            OutputIndex = transcriptDone.OutputIndex,
            ContentIndex = transcriptDone.ContentIndex,
            Text = transcriptDone.Transcript,
            RawRepresentation = transcriptDone,
        },
        SessionUpdateConversationItemInputAudioTranscriptionDelta delta => MapInputTranscriptionDelta(delta),
        SessionUpdateConversationItemInputAudioTranscriptionCompleted completed => MapInputTranscriptionCompleted(completed),
        SessionUpdateConversationItemInputAudioTranscriptionFailed failed => MapInputTranscriptionFailed(failed),
        SessionUpdateConversationItemCreated created => MapConversationItem(created.EventId, created.Item, RealtimeServerMessageType.ResponseOutputItemAdded, created),
        SessionUpdateConversationItemRetrieved retrieved => MapConversationItem(retrieved.EventId, retrieved.Item, RealtimeServerMessageType.ResponseOutputItemDone, retrieved),
        SessionUpdateInputAudioBufferSpeechStarted inputSpeechStarted => new InputAudioBufferSpeechRealtimeServerMessage(InputAudioBufferSpeechRealtimeServerMessage.InputAudioBufferSpeechStarted)
        {
            MessageId = inputSpeechStarted.EventId,
            ItemId = inputSpeechStarted.ItemId,
            AudioStart = inputSpeechStarted.AudioStart,
            RawRepresentation = inputSpeechStarted,
        },
        SessionUpdateInputAudioBufferSpeechStopped inputSpeechStopped => new InputAudioBufferSpeechRealtimeServerMessage(InputAudioBufferSpeechRealtimeServerMessage.InputAudioBufferSpeechStopped)
        {
            MessageId = inputSpeechStopped.EventId,
            ItemId = inputSpeechStopped.ItemId,
            AudioEnd = inputSpeechStopped.AudioEnd,
            RawRepresentation = inputSpeechStopped,
        },
        SessionUpdateResponseMcpCallInProgress inProgress => MapMcpCallEvent(inProgress.EventId, inProgress.ItemId, inProgress.OutputIndex, new RealtimeServerMessageType("McpCallInProgress"), inProgress),
        SessionUpdateResponseMcpCallCompleted completed => MapMcpCallEvent(completed.EventId, completed.ItemId, completed.OutputIndex, new RealtimeServerMessageType("McpCallCompleted"), completed),
        SessionUpdateResponseMcpCallFailed failed => MapMcpCallEvent(failed.EventId, failed.ItemId, failed.OutputIndex, new RealtimeServerMessageType("McpCallFailed"), failed),
        SessionUpdateMcpListToolsInProgress inProgress => MapMcpListToolsEvent(inProgress.EventId, inProgress.ItemId, new RealtimeServerMessageType("McpListToolsInProgress"), inProgress),
        SessionUpdateMcpListToolsCompleted completed => MapMcpListToolsEvent(completed.EventId, completed.ItemId, new RealtimeServerMessageType("McpListToolsCompleted"), completed),
        SessionUpdateMcpListToolsFailed failed => MapMcpListToolsEvent(failed.EventId, failed.ItemId, new RealtimeServerMessageType("McpListToolsFailed"), failed),
        _ => new RealtimeServerMessage
        {
            Type = RealtimeServerMessageType.RawContentOnly,
            MessageId = update.EventId,
            RawRepresentation = update,
        },
    };

    private static AudioInputTranscriptionOptionsModel ToVoiceLiveTranscriptionModel(string? modelId) => modelId?.ToLowerInvariant() switch
    {
        "whisper-1" => AudioInputTranscriptionOptionsModel.Whisper1,
        "gpt-4o-transcribe" => AudioInputTranscriptionOptionsModel.Gpt4oTranscribe,
        "gpt-4o-mini-transcribe" => AudioInputTranscriptionOptionsModel.Gpt4oMiniTranscribe,
        "azure-speech" => AudioInputTranscriptionOptionsModel.AzureSpeech,
        _ => AudioInputTranscriptionOptionsModel.Gpt4oMiniTranscribe,
    };

    private static ErrorRealtimeServerMessage MapError(SessionUpdateError error)
    {
        var message = new ErrorRealtimeServerMessage
        {
            MessageId = error.EventId,
            RawRepresentation = error,
        };

        if (error.Error is not null)
        {
            message.Error = new ErrorContent(error.Error.Message)
            {
                ErrorCode = error.Error.Code,
                Details = error.Error.Param,
            };
            message.OriginatingMessageId = error.Error.EventId;
        }

        return message;
    }

    private RealtimeServerMessage HandleSessionEvent(VoiceLiveSessionResponse? session, SessionUpdate update)
    {
        if (session is not null)
        {
            // Merge the server-reported state into the current effective options so the
            // Options property always reflects the latest known configuration without
            // losing client-side fields the response may not echo (for example, Tools).
            Options = MergeOptions(Options, MapSessionToOptions(session));
        }

        return new RealtimeServerMessage
        {
            Type = RealtimeServerMessageType.RawContentOnly,
            MessageId = update.EventId,
            RawRepresentation = update,
        };
    }

    private static RealtimeSessionOptions MapSessionToOptions(VoiceLiveSessionResponse session)
    {
        List<string>? outputModalities = null;
        if (session.Modalities is { Count: > 0 } modalities)
        {
            outputModalities = modalities.Select(m => m.ToString()).ToList();
        }

        TranscriptionOptions? transcriptionOptions = null;
        if (session.InputAudioTranscription is { } transcription)
        {
            transcriptionOptions = new TranscriptionOptions
            {
                SpeechLanguage = transcription.Language,
                ModelId = transcription.Model.ToString(),
            };
        }

        // Only project fields the SDK response actually carries. Anything left null here
        // will be preserved from the existing Options by MergeOptions, matching Voice
        // Live's "only fields that are present are updated" semantics.
        return new RealtimeSessionOptions
        {
            SessionKind = RealtimeSessionKind.Conversation,
            Model = session.Model,
            Instructions = session.Instructions,
            MaxOutputTokens = session.MaxResponseOutputTokens?.NumericValue,
            OutputModalities = outputModalities,
            InputAudioFormat = MapSdkAudioFormat(session.InputAudioFormat, session.InputAudioSamplingRate),
            TranscriptionOptions = transcriptionOptions,
            OutputAudioFormat = MapSdkAudioFormat(session.OutputAudioFormat),
            Voice = GetVoiceName(session.Voice),
        };
    }

    private static ResponseCreatedRealtimeServerMessage MapResponseCreatedOrDone(
        string? eventId,
        SessionResponse? response,
        RealtimeServerMessageType type,
        SessionUpdate update)
    {
        var message = new ResponseCreatedRealtimeServerMessage(type)
        {
            MessageId = eventId,
            RawRepresentation = update,
        };

        if (response is null)
        {
            return message;
        }

        message.ResponseId = response.Id;
        message.Status = response.Status.ToString();
        message.OutputAudioOptions = MapSdkAudioFormat(response.OutputAudioFormat);
        message.OutputVoice = GetVoiceName(response.Voice);
        message.MaxOutputTokens = response.MaxOutputTokens.NumericValue;

        if (response.Metadata is { Count: > 0 } metadata)
        {
            var additionalProperties = new AdditionalPropertiesDictionary();
            foreach (var kvp in metadata)
            {
                additionalProperties[kvp.Key] = kvp.Value;
            }

            message.AdditionalProperties = additionalProperties;
        }

        if (response.Modalities is { Count: > 0 } modalities)
        {
            message.OutputModalities = modalities.Select(m => m.ToString()).ToList();
        }

        if (response.StatusDetails is { } statusDetails)
        {
            message.Error = new ErrorContent(statusDetails.ToString());
        }

        if (response.Usage is { } usage)
        {
            message.Usage = MapUsageDetails(usage);
        }

        if (response.Output is { Count: > 0 } outputItems)
        {
            var items = new List<RealtimeConversationItem>();
            foreach (var item in outputItems)
            {
                if (MapRealtimeItem(item) is { } mappedItem)
                {
                    items.Add(mappedItem);
                }
            }

            message.Items = items;
        }

        return message;
    }

    private static ResponseOutputItemRealtimeServerMessage MapResponseOutputItem(
        string? eventId,
        string? responseId,
        int outputIndex,
        SessionResponseItem? item,
        RealtimeServerMessageType type,
        SessionUpdate update)
    {
        return new ResponseOutputItemRealtimeServerMessage(type)
        {
            MessageId = eventId,
            ResponseId = responseId,
            OutputIndex = outputIndex,
            Item = item is not null ? MapRealtimeItem(item) : null,
            RawRepresentation = update,
        };
    }

    private static ResponseOutputItemRealtimeServerMessage MapConversationItem(
        string? eventId,
        SessionResponseItem? item,
        RealtimeServerMessageType type,
        SessionUpdate update)
    {
        var mapped = item is not null ? MapRealtimeItem(item) : null;
        if (mapped is null)
        {
            return new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.RawContentOnly)
            {
                MessageId = eventId,
                RawRepresentation = update,
            };
        }

        return new ResponseOutputItemRealtimeServerMessage(type)
        {
            MessageId = eventId,
            Item = mapped,
            RawRepresentation = update,
        };
    }

    private static InputAudioTranscriptionRealtimeServerMessage MapInputTranscriptionDelta(SessionUpdateConversationItemInputAudioTranscriptionDelta update)
    {
        return new InputAudioTranscriptionRealtimeServerMessage(RealtimeServerMessageType.InputAudioTranscriptionDelta)
        {
            MessageId = update.EventId,
            ItemId = update.ItemId,
            ContentIndex = update.ContentIndex,
            Transcription = update.Delta,
            RawRepresentation = update,
        };
    }

    private static InputAudioTranscriptionRealtimeServerMessage MapInputTranscriptionCompleted(SessionUpdateConversationItemInputAudioTranscriptionCompleted update)
    {
        return new InputAudioTranscriptionRealtimeServerMessage(RealtimeServerMessageType.InputAudioTranscriptionCompleted)
        {
            MessageId = update.EventId,
            ItemId = update.ItemId,
            ContentIndex = update.ContentIndex,
            Transcription = update.Transcript,
            RawRepresentation = update,
        };
    }

    private static InputAudioTranscriptionRealtimeServerMessage MapInputTranscriptionFailed(SessionUpdateConversationItemInputAudioTranscriptionFailed update)
    {
        var message = new InputAudioTranscriptionRealtimeServerMessage(RealtimeServerMessageType.InputAudioTranscriptionFailed)
        {
            MessageId = update.EventId,
            ItemId = update.ItemId,
            ContentIndex = update.ContentIndex,
            RawRepresentation = update,
        };

        if (update.Error is not null)
        {
            message.Error = new ErrorContent(update.Error.Message)
            {
                ErrorCode = update.Error.Code,
                Details = update.Error.Param,
            };
        }

        return message;
    }

    private static ResponseOutputItemRealtimeServerMessage MapMcpCallEvent(
        string? eventId,
        string? itemId,
        int outputIndex,
        RealtimeServerMessageType type,
        SessionUpdate update)
    {
        return new ResponseOutputItemRealtimeServerMessage(type)
        {
            MessageId = eventId,
            Item = itemId is not null ? new RealtimeConversationItem([], itemId) : null,
            OutputIndex = outputIndex,
            RawRepresentation = update,
        };
    }

    private static ResponseOutputItemRealtimeServerMessage MapMcpListToolsEvent(
        string? eventId,
        string? itemId,
        RealtimeServerMessageType type,
        SessionUpdate update)
    {
        return new ResponseOutputItemRealtimeServerMessage(type)
        {
            MessageId = eventId,
            Item = itemId is not null ? new RealtimeConversationItem([], itemId) : null,
            RawRepresentation = update,
        };
    }

    private static RealtimeConversationItem? MapConversationRequestItem(ConversationRequestItem item) => item switch
    {
        MessageItem messageItem => MapRequestMessageItem(messageItem),
        FunctionCallItem functionCallItem => MapRequestFunctionCallItem(functionCallItem),
        FunctionCallOutputItem functionOutputItem => new RealtimeConversationItem(
            [new FunctionResultContent(functionOutputItem.CallId ?? string.Empty, functionOutputItem.Output)],
            functionOutputItem.Id),
        _ => null,
    };

    private static RealtimeConversationItem? MapRealtimeItem(SessionResponseItem item) => item switch
    {
        SessionResponseMessageItem messageItem => MapResponseMessageItem(messageItem),
        ResponseFunctionCallItem functionCallItem => MapResponseFunctionCallItem(functionCallItem),
        ResponseFunctionCallOutputItem functionOutputItem => new RealtimeConversationItem(
            [new FunctionResultContent(functionOutputItem.CallId ?? string.Empty, functionOutputItem.Output)],
            functionOutputItem.Id),
        SessionResponseMcpCallItem mcpItem => MapMcpToolCallItem(mcpItem),
        SessionResponseMcpApprovalRequestItem approvalItem => MapMcpApprovalRequestItem(approvalItem),
        SessionResponseMcpListToolItem toolListItem => MapMcpToolDefinitionListItem(toolListItem),
        _ => null,
    };

    private static RealtimeConversationItem MapRequestFunctionCallItem(FunctionCallItem functionCallItem)
    {
        IDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(functionCallItem.Arguments))
        {
            arguments = JsonSerializer.Deserialize(functionCallItem.Arguments, OpenAIRealtimeJsonContext.Default.IDictionaryStringObject);
        }

        return new RealtimeConversationItem(
            [new FunctionCallContent(functionCallItem.CallId ?? string.Empty, functionCallItem.Name, arguments)],
            functionCallItem.Id);
    }

    private static RealtimeConversationItem MapResponseFunctionCallItem(ResponseFunctionCallItem functionCallItem)
    {
        IDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(functionCallItem.Arguments))
        {
            arguments = JsonSerializer.Deserialize(functionCallItem.Arguments, OpenAIRealtimeJsonContext.Default.IDictionaryStringObject);
        }

        return new RealtimeConversationItem(
            [new FunctionCallContent(functionCallItem.CallId ?? string.Empty, functionCallItem.Name, arguments)],
            functionCallItem.Id);
    }

    private static RealtimeConversationItem MapRequestMessageItem(MessageItem messageItem)
    {
        var contents = new List<AIContent>();
        foreach (var part in messageItem.Content)
        {
            if (part is InputTextContentPart textPart)
            {
                contents.Add(new TextContent(textPart.Text));
            }
            else if (part is InputAudioContentPart audioPart && !string.IsNullOrEmpty(audioPart.Audio))
            {
                contents.Add(new DataContent($"data:audio/pcm;base64,{audioPart.Audio}"));
            }
        }

        ChatRole? role = messageItem switch
        {
            AssistantMessageItem => ChatRole.Assistant,
            SystemMessageItem => ChatRole.System,
            UserMessageItem => ChatRole.User,
            _ => null,
        };

        return new RealtimeConversationItem(contents, messageItem.Id, role);
    }

    private static RealtimeConversationItem MapResponseMessageItem(SessionResponseMessageItem messageItem)
    {
        var contents = new List<AIContent>();
        foreach (var part in messageItem.Content)
        {
            if (part is ResponseTextContentPart outputTextPart)
            {
                contents.Add(new TextContent(outputTextPart.Text));
            }
            else if (part is ResponseAudioContentPart outputAudioPart && outputAudioPart.Transcript is not null)
            {
                contents.Add(new TextContent(outputAudioPart.Transcript));
            }
        }

        return new RealtimeConversationItem(contents, messageItem.Id, MapMessageRole(messageItem.Role));
    }

    private static RealtimeConversationItem MapMcpToolCallItem(SessionResponseMcpCallItem mcpItem)
    {
        IDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(mcpItem.Arguments))
        {
            arguments = JsonSerializer.Deserialize(mcpItem.Arguments, OpenAIRealtimeJsonContext.Default.IDictionaryStringObject);
        }

        var contents = new List<AIContent>
        {
            new McpServerToolCallContent(mcpItem.Id ?? string.Empty, mcpItem.Name ?? string.Empty, mcpItem.ServerLabel)
            {
                Arguments = arguments?.AsReadOnly(),
                RawRepresentation = mcpItem,
            },
        };

        if (mcpItem.Output is not null || mcpItem.Error is not null)
        {
            AIContent resultContent = mcpItem.Error is not null
                ? new ErrorContent(mcpItem.Error.ToString())
                : new TextContent(mcpItem.Output);

            contents.Add(new McpServerToolResultContent(mcpItem.Id ?? string.Empty)
            {
                Outputs = [resultContent],
                RawRepresentation = mcpItem,
            });
        }

        return new RealtimeConversationItem(contents, mcpItem.Id);
    }

    private static RealtimeConversationItem MapMcpApprovalRequestItem(SessionResponseMcpApprovalRequestItem approvalItem)
    {
        IDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrEmpty(approvalItem.Arguments))
        {
            arguments = JsonSerializer.Deserialize(approvalItem.Arguments, OpenAIRealtimeJsonContext.Default.IDictionaryStringObject);
        }

        var toolCall = new McpServerToolCallContent(approvalItem.Id ?? string.Empty, approvalItem.Name ?? string.Empty, approvalItem.ServerLabel)
        {
            Arguments = arguments?.AsReadOnly(),
            RawRepresentation = approvalItem,
        };

        return new RealtimeConversationItem(
            [new ToolApprovalRequestContent(approvalItem.Id ?? string.Empty, toolCall) { RawRepresentation = approvalItem }],
            approvalItem.Id);
    }

    private static RealtimeConversationItem MapMcpToolDefinitionListItem(SessionResponseMcpListToolItem toolListItem)
    {
        var contents = new List<AIContent>();
        foreach (var tool in toolListItem.Tools)
        {
            if (tool.Name is not null)
            {
                contents.Add(new McpServerToolCallContent(tool.Name, tool.Name, toolListItem.ServerLabel)
                {
                    RawRepresentation = tool,
                });
            }
        }

        return new RealtimeConversationItem(contents, toolListItem.Id);
    }

    private static UsageDetails? MapUsageDetails(ResponseTokenStatistics? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            TotalTokenCount = usage.TotalTokens,
        };
    }

    private static ChatRole? MapMessageRole(ResponseMessageRole role) => role switch
    {
        var r when r == ResponseMessageRole.Assistant => ChatRole.Assistant,
        var r when r == ResponseMessageRole.System => ChatRole.System,
        var r when r == ResponseMessageRole.User => ChatRole.User,
        _ => null,
    };

    private static RealtimeAudioFormat? MapSdkAudioFormat(InputAudioFormat? format, int? samplingRate = null) => format?.ToString() switch
    {
        "pcm16" or "Pcm16" => new RealtimeAudioFormat("audio/pcm", samplingRate ?? 16000),
        "g711_ulaw" or "G711Ulaw" => new RealtimeAudioFormat("audio/pcmu", 8000),
        "g711_alaw" or "G711Alaw" => new RealtimeAudioFormat("audio/pcma", 8000),
        _ => null,
    };

    private static RealtimeAudioFormat? MapSdkAudioFormat(OutputAudioFormat? format) => format?.ToString() switch
    {
        "pcm16" or "Pcm16" => new RealtimeAudioFormat("audio/pcm", 16000),
        "g711_ulaw" or "G711Ulaw" => new RealtimeAudioFormat("audio/pcmu", 8000),
        "g711_alaw" or "G711Alaw" => new RealtimeAudioFormat("audio/pcma", 8000),
        _ => null,
    };

    private static string? GetVoiceName(VoiceProvider? voice) => voice switch
    {
        AzureStandardVoice azureStandardVoice => azureStandardVoice.Name,
        AzureCustomVoice azureCustomVoice => azureCustomVoice.Name,
        _ => voice?.ToString(),
    };

    #endregion
}
