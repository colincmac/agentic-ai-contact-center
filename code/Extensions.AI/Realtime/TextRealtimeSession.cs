using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.Realtime;

/// <summary>
/// A text-based fallback implementation of <see cref="IRealtimeClientSession"/> that orchestrates
/// STT → Chat → TTS to simulate a realtime voice conversation when native realtime services are unavailable.
/// </summary>
public class TextRealtimeSession : IRealtimeClientSession
{
    /// <summary>Whether the session has been disposed (0 = false, 1 = true).</summary>
    private int _disposed;

    /// <summary>Gets the inner <see cref="ITextToSpeechClient"/>.</summary>
    protected ITextToSpeechClient InnerTtsClient { get; }

    /// <summary>Gets the inner <see cref="IChatClient"/>.</summary>
    protected IChatClient InnerChatClient { get; }

    /// <summary>Gets the inner <see cref="ISpeechToTextClient"/>.</summary>
    protected ISpeechToTextClient InnerSttClient { get; }

    private readonly Channel<RealtimeServerMessage> _outgoingMessageChannel;
    private readonly List<ChatMessage> _conversationHistory = [];
    private readonly MemoryStream _audioBuffer = new();
    private readonly Lock _audioBufferLock = new();

    public TextRealtimeSession(ISpeechToTextClient speechToTextClient, ITextToSpeechClient textToSpeechClient, IChatClient chatClient)
    {
        InnerSttClient = Throw.IfNull(speechToTextClient);
        InnerTtsClient = Throw.IfNull(textToSpeechClient);
        InnerChatClient = Throw.IfNull(chatClient);
        _outgoingMessageChannel = Channel.CreateUnbounded<RealtimeServerMessage>();
    }

    /// <inheritdoc />
    public RealtimeSessionOptions? Options { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _outgoingMessageChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
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
                    UpdateSession(sessionUpdate.Options);
                    break;

                case CreateConversationItemRealtimeClientMessage itemCreate:
                    AddConversationItem(itemCreate);
                    break;

                case InputAudioBufferAppendRealtimeClientMessage audioAppend:
                    AppendAudioBuffer(audioAppend);
                    break;

                case InputAudioBufferCommitRealtimeClientMessage:
                    await ProcessAudioPipelineAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case CreateResponseRealtimeClientMessage responseCreate:
                    await ProcessResponseCreateAsync(responseCreate, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // Expected during session teardown or cancellation.
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        return
            serviceKey is not null ? null :
            serviceType.IsInstanceOfType(this) ? this :
            serviceType.IsInstanceOfType(InnerChatClient) ? InnerChatClient :
            serviceType.IsInstanceOfType(InnerSttClient) ? InnerSttClient :
            serviceType.IsInstanceOfType(InnerTtsClient) ? InnerTtsClient :
            null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return default;
        }

        _outgoingMessageChannel.Writer.TryComplete();
        _audioBuffer.Dispose();

        return default;
    }

    #region Session Management

    private void UpdateSession(RealtimeSessionOptions options)
    {
        Options = options;

        if (options.Instructions is not null)
        {
            if (_conversationHistory.Count > 0 && _conversationHistory[0].Role == ChatRole.System)
            {
                _conversationHistory[0] = new ChatMessage(ChatRole.System, options.Instructions);
            }
            else
            {
                _conversationHistory.Insert(0, new ChatMessage(ChatRole.System, options.Instructions));
            }
        }
    }

    private void AddConversationItem(CreateConversationItemRealtimeClientMessage itemCreate)
    {
        if (itemCreate.Item?.Contents is not { Count: > 0 })
        {
            return;
        }

        var role = itemCreate.Item.Role ?? ChatRole.User;
        var chatMessage = new ChatMessage(role, []);

        foreach (var content in itemCreate.Item.Contents)
        {
            if (content is TextContent textContent)
            {
                chatMessage.Contents.Add(textContent);
            }
        }

        if (chatMessage.Contents.Count > 0)
        {
            _conversationHistory.Add(chatMessage);
        }
    }

    #endregion

    #region Audio Buffer

    private void AppendAudioBuffer(InputAudioBufferAppendRealtimeClientMessage audioAppend)
    {
        if (audioAppend.Content is null || !audioAppend.Content.HasTopLevelMediaType("audio"))
        {
            return;
        }

        var audioBytes = ExtractAudioBinaryData(audioAppend.Content).ToArray();

        lock (_audioBufferLock)
        {
            _audioBuffer.Write(audioBytes);
        }
    }

    private byte[] DrainAudioBuffer()
    {
        lock (_audioBufferLock)
        {
            var data = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);

            return data;
        }
    }

    #endregion

    #region Pipeline: STT → Chat → TTS

    /// <summary>
    /// Drains the accumulated audio buffer, transcribes it via STT, then runs chat + TTS.
    /// </summary>
    private async Task ProcessAudioPipelineAsync(CancellationToken cancellationToken)
    {
        var audioData = DrainAudioBuffer();
        if (audioData.Length == 0)
        {
            return;
        }

        // Step 1: STT — transcribe the accumulated audio to text
        string transcribedText;
        try
        {
            using var audioStream = new MemoryStream(audioData);
            var sttResponse = await InnerSttClient.GetTextAsync(audioStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            transcribedText = sttResponse.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitErrorAsync("stt_error", $"Speech-to-text failed: {ex.Message}", cancellationToken).ConfigureAwait(false);

            return;
        }

        if (string.IsNullOrWhiteSpace(transcribedText))
        {
            return;
        }

        // Emit input audio transcription completed
        await WriteServerMessageAsync(
            new InputAudioTranscriptionRealtimeServerMessage(RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                MessageId = GenerateId("evt"),
                ItemId = GenerateId("item"),
                ContentIndex = 0,
                Transcription = transcribedText,
            }, cancellationToken).ConfigureAwait(false);

        // Add user message to conversation history
        _conversationHistory.Add(new ChatMessage(ChatRole.User, transcribedText));

        // Steps 2 & 3: Chat completion then TTS
        await ProcessChatAndTtsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an explicit <see cref="CreateResponseRealtimeClientMessage"/> by adding any
    /// included items to the conversation and triggering chat + TTS.
    /// </summary>
    private async Task ProcessResponseCreateAsync(CreateResponseRealtimeClientMessage responseCreate, CancellationToken cancellationToken)
    {
        if (responseCreate.Items is not null)
        {
            foreach (var item in responseCreate.Items)
            {
                if (item.Contents is not { Count: > 0 })
                {
                    continue;
                }

                var role = item.Role ?? ChatRole.User;
                var chatMessage = new ChatMessage(role, []);

                foreach (var content in item.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent:
                            chatMessage.Contents.Add(textContent);
                            break;
                        case FunctionResultContent functionResult:
                            chatMessage.Contents.Add(functionResult);
                            break;
                    }
                }

                if (chatMessage.Contents.Count > 0)
                {
                    _conversationHistory.Add(chatMessage);
                }
            }
        }

        if (responseCreate.Instructions is not null)
        {
            if (_conversationHistory.Count > 0 && _conversationHistory[0].Role == ChatRole.System)
            {
                _conversationHistory[0] = new ChatMessage(ChatRole.System, responseCreate.Instructions);
            }
            else
            {
                _conversationHistory.Insert(0, new ChatMessage(ChatRole.System, responseCreate.Instructions));
            }
        }

        await ProcessChatAndTtsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core pipeline: streams a chat completion (emitting text deltas), then streams TTS audio
    /// (emitting audio deltas), and finally emits all completion server messages.
    /// </summary>
    private async Task ProcessChatAndTtsAsync(CancellationToken cancellationToken)
    {
        var responseId = GenerateId("resp");
        var outputItemId = GenerateId("item");

        // Signal response created
        await WriteServerMessageAsync(
            new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseCreated)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                Status = "in_progress",
            }, cancellationToken).ConfigureAwait(false);

        // Signal output item added
        await WriteServerMessageAsync(
            new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemAdded)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                OutputIndex = 0,
                Item = new RealtimeConversationItem([new TextContent(string.Empty)], outputItemId, ChatRole.Assistant),
            }, cancellationToken).ConfigureAwait(false);

        // Step 2: Stream chat completion, emitting text deltas
        var chatOptions = BuildChatOptions();
        var fullText = new StringBuilder();

        try
        {

            await foreach (var update in InnerChatClient.GetStreamingResponseAsync(_conversationHistory, chatOptions, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    fullText.Append(update.Text);
                    await WriteServerMessageAsync(
                        new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputTextDelta)
                        {
                            MessageId = update.MessageId,
                            ResponseId = responseId,
                            ItemId = outputItemId,
                            OutputIndex = 0,
                            ContentIndex = 0,
                            Text = update.Text,
                        }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitErrorAsync("chat_error", $"Chat completion failed: {ex.Message}", cancellationToken).ConfigureAwait(false);

            return;
        }

        var responseText = fullText.ToString();

        // Emit text done
        await WriteServerMessageAsync(
            new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputTextDone)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                ItemId = outputItemId,
                OutputIndex = 0,
                ContentIndex = 0,
                Text = responseText,
            }, cancellationToken).ConfigureAwait(false);

        // Add assistant response to conversation history
        _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));

        // Step 3: TTS — stream synthesized audio back
        if (!string.IsNullOrEmpty(responseText))
        {
            try
            {
                await foreach (var audioUpdate in InnerTtsClient.GetStreamingAudioAsync(responseText, BuildTtsOptions(), cancellationToken).ConfigureAwait(false))
                {
                    if (audioUpdate.Contents is not { Count: > 0 })
                    {
                        continue;
                    }

                    foreach (var content in audioUpdate.Contents)
                    {
                        if (content is DataContent dataContent && dataContent.HasTopLevelMediaType("audio"))
                        {
                            var base64Audio = Convert.ToBase64String(ExtractAudioBinaryData(dataContent).ToArray());

                            await WriteServerMessageAsync(
                                new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
                                {
                                    MessageId = GenerateId("evt"),
                                    ResponseId = responseId,
                                    ItemId = outputItemId,
                                    OutputIndex = 0,
                                    ContentIndex = 0,
                                    Audio = base64Audio,
                                }, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await EmitErrorAsync("tts_error", $"Text-to-speech failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        // Emit audio done
        await WriteServerMessageAsync(
            new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDone)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                ItemId = outputItemId,
                OutputIndex = 0,
                ContentIndex = 0,
            }, cancellationToken).ConfigureAwait(false);

        // Emit output audio transcription done (the chat text IS the transcription)
        await WriteServerMessageAsync(
            new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioTranscriptionDone)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                ItemId = outputItemId,
                OutputIndex = 0,
                ContentIndex = 0,
                Text = responseText,
            }, cancellationToken).ConfigureAwait(false);

        // Signal output item done
        await WriteServerMessageAsync(
            new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemDone)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                OutputIndex = 0,
                Item = new RealtimeConversationItem([new TextContent(responseText)], outputItemId, ChatRole.Assistant),
            }, cancellationToken).ConfigureAwait(false);

        // Signal response done
        await WriteServerMessageAsync(
            new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
            {
                MessageId = GenerateId("evt"),
                ResponseId = responseId,
                Status = "completed",
            }, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Helpers

    private ChatOptions? BuildChatOptions()
    {
        if (Options is null)
        {
            return null;
        }

        var chatOptions = new ChatOptions();

        if (Options.Model is not null)
        {
            chatOptions.ModelId = Options.Model;
        }

        if (Options.MaxOutputTokens.HasValue)
        {
            chatOptions.MaxOutputTokens = Options.MaxOutputTokens.Value;
        }

        if (Options.Tools is { Count: > 0 })
        {
            chatOptions.Tools = [.. Options.Tools];
        }

        if (Options.ToolMode is not null)
        {
            chatOptions.ToolMode = Options.ToolMode;
        }

        return chatOptions;
    }

    private TextToSpeechOptions? BuildTtsOptions()
    {
        if (Options?.Voice is null)
        {
            return null;
        }

        return new TextToSpeechOptions
        {
            VoiceId = Options.Voice,
        };
    }

    private async Task WriteServerMessageAsync(RealtimeServerMessage message, CancellationToken cancellationToken)
    {
        await _outgoingMessageChannel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitErrorAsync(string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        await WriteServerMessageAsync(
            new ErrorRealtimeServerMessage
            {
                MessageId = GenerateId("evt"),
                Error = new ErrorContent(errorMessage) { ErrorCode = errorCode },
            }, cancellationToken).ConfigureAwait(false);
    }

    private static string GenerateId(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}";
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
}
