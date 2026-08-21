using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Extensions.AI.OpenTelemetry.SemanticConventions;
using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Extensions.AI.ExtensionsAIJsonUtilities;

namespace Extensions.AI.RealtimeVoice;

/// <summary>
/// An <see cref="ILiveConversationSession"/> decorator that emits OpenTelemetry
/// activities and metrics for real-time / live voice conversation scenarios,
/// following the (experimental) OpenTelemetry Semantic Conventions for GenAI.
/// </summary>
/// <remarks>
/// This implementation mirrors patterns used by <see cref="OpenTelemetryChatClient"/>
/// for standard chat completions, adapted to the live / streaming session model.
/// The telemetry surface may evolve as the underlying conventions evolve.
/// </remarks>
public sealed partial class OpenTelemetryConversationSession : DelegatingConversationSession
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;

    private readonly Histogram<int> _tokenUsageHistogram;
    private readonly Histogram<double> _operationDurationHistogram;

    ILogger? _logger;
    private readonly string? _modelId;
    private readonly string? _providerName;
    private readonly string? _serverAddress;
    private readonly int _serverPort;

    /// <summary>
    /// Creates a new <see cref="OpenTelemetryConversationSession"/>.
    /// </summary>
    /// <param name="innerSession">The underlying session to decorate.</param>
    /// <param name="sourceName">Optional source name (defaults to <see cref="TelemetryHelpers.DefaultSourceName"/>).</param>
    /// <param name="metadata">
    /// Optional chat client metadata (if available from the creating component) used for model and provider tagging.
    /// If null, model/provider/server tags are simply omitted.
    /// </param>
    public OpenTelemetryConversationSession(
        ILiveConversationSession innerSession,
        string? sourceName = null,
        ILogger? logger = null
        )
        : base(innerSession)
    {
        var metadata = innerSession.GetService<LiveConversationSessionMetadata>();
        _modelId = metadata?.ModelId;
        _providerName = metadata?.ProviderName;
        _serverAddress = metadata?.ProviderUri?.Host;
        _serverPort = metadata?.ProviderUri?.Port ?? 0;
        _logger = logger ?? NullLogger.Instance;
        var name = string.IsNullOrEmpty(sourceName) ? TelemetryHelpers.DefaultSourceName : sourceName!;
        _activitySource = new(name);
        _meter = new(name);

        _tokenUsageHistogram = _meter.CreateHistogram<int>(
            GenAI.Client.TokenUsage.Name,
            GenAI.TokensUnit,
            GenAI.Client.TokenUsage.Description
#if NET9_0_OR_GREATER
            , advice: new() { HistogramBucketBoundaries = GenAI.Client.TokenUsage.ExplicitBucketBoundaries }
#endif
            );

        _operationDurationHistogram = _meter.CreateHistogram<double>(
            GenAI.Client.OperationDuration.Name,
            GenAI.SecondsUnit,
            GenAI.Client.OperationDuration.Description
#if NET9_0_OR_GREATER
            , advice: new() { HistogramBucketBoundaries = GenAI.Client.OperationDuration.ExplicitBucketBoundaries }
#endif
            );
    }

    #region Pass-through members

    public override string? SessionId => base.SessionId;
    public override RealtimeSessionState State => base.State;
    public override event EventHandler<RealtimeSessionStateChangedEventArgs>? StateChanged
    {
        add => base.StateChanged += value;
        remove => base.StateChanged -= value;
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    #endregion
    /// <summary>
    /// Gets or sets a value indicating whether potentially sensitive data (raw message content)
    /// should be captured in telemetry.
    /// Defaults to environment-controlled value set by
    /// OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT (see <see cref="TelemetryHelpers.EnableSensitiveDataDefault"/>).
    /// </summary>
    public bool EnableSensitiveData { get; set; } = TelemetryHelpers.EnableSensitiveDataDefault;

    /// <summary>
    /// Returns the activity source so tracing exporters can access it.
    /// </summary>
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ActivitySource) ? _activitySource :
        serviceType == typeof(OpenTelemetryConversationSession) ? this :
        base.GetService(serviceType, serviceKey);

    #region ILiveConversationSession overrides (instrumented)

    public override Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "send_audio",
            action: () => base.SendAudioAsync(audioData, cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken);

    public override Task SendAudioStreamAsync(Stream audioStream, CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "send_audio_stream",
            action: () => base.SendAudioStreamAsync(audioStream, cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken);

    public override Task SendMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "send_message",
            action: () => base.SendMessagesAsync(messages, cancellationToken),
            inputMessages: messages,
            cancellationToken: cancellationToken);

    public override Task InterruptAsync(CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "interrupt",
            action: () => base.InterruptAsync(cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken);

    public override Task CommitPendingAudioAsync(CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "commit_audio",
            action: () => base.CommitPendingAudioAsync(cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken);

    public override Task ClearInputAudioAsync(CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "clear_audio",
            action: () => base.ClearInputAudioAsync(cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken);

    public override Task StartResponseAsync(LiveConversationResponseOptions? responseOptions, CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "start_response",
            action: () => base.StartResponseAsync(responseOptions, cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken,
            additionalTags: activity =>
            {

                if (responseOptions?.AdditionalProperties is { } props && EnableSensitiveData)
                {
                    foreach (var kv in props)
                    {
                        activity?.AddTag(kv.Key, kv.Value);
                    }
                }
            });

    public override Task ConfigureSessionAsync(LiveConversationSessionOptions options, CancellationToken cancellationToken = default) =>
        TraceSimpleOperationAsync(
            operationName: "configure_session",
            action: () => base.ConfigureSessionAsync(options, cancellationToken),
            inputMessages: null,
            cancellationToken: cancellationToken,
            additionalTags: activity =>
            {
                if (options.Voice is { } voice)
                {
                    activity?.AddTag(GenAI.Realtime.Voice, voice);
                }
                if (options.AdditionalProperties is { } props && EnableSensitiveData)
                {
                    foreach (var kv in props)
                    {
                        activity?.AddTag(kv.Key, kv.Value);
                    }
                }
            });

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(LiveConversationResponseOptions? options = null, CancellationToken cancellationToken = default) =>
        ReceiveUpdatesWithTracingAsync(options, cancellationToken);

    #endregion

    #region Tracing Helpers

    private async Task TraceSimpleOperationAsync(
        string operationName,
        Func<Task> action,
        IEnumerable<ChatMessage>? inputMessages,
        CancellationToken cancellationToken,
        Action<Activity?>? additionalTags = null)
    {
        using var activity = StartActivity(operationName);
        var stopwatch = _operationDurationHistogram.Enabled ? Stopwatch.StartNew() : null;

        if (activity is { IsAllDataRequested: true } && inputMessages is not null)
        {
            AddInputMessagesTags(inputMessages, activity);
        }

        Exception? error = null;
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            additionalTags?.Invoke(activity!);
            CompleteActivity(activity, error, stopwatch, requestModelId: null, response: null);
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> ReceiveUpdatesWithTracingAsync(LiveConversationResponseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity("receive_updates");
        var stopwatch = _operationDurationHistogram.Enabled ? Stopwatch.StartNew() : null;

        List<ChatResponseUpdate> trackedUpdates = [];
        Exception? error = null;
        IAsyncEnumerator<ChatResponseUpdate> enumerator;

        try
        {
            enumerator = base.GetStreamingResponseAsync(options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }

        var hasUpdates = await enumerator.MoveNextAsync().ConfigureAwait(false);

        while (hasUpdates)
        {
            var update = enumerator.Current;

            trackedUpdates.Add(update);
            yield return update;
            // Ensure activity flow for async enumeration
            Activity.Current = activity;

            hasUpdates = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        ChatResponse? finalResponse = trackedUpdates.ToChatResponse();
        CompleteActivity(activity, error, stopwatch, requestModelId: finalResponse?.ModelId, response: finalResponse);
        await enumerator.DisposeAsync().ConfigureAwait(false);
    }

    private Activity? StartActivity(string shortOperationName)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }
        string activityName = $"{GenAI.OperationNameValues.GenerateContent} {_modelId}";
        var activity = _activitySource.StartActivity(activityName, ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            activity
                .AddTag(GenAI.AttributeGenAiOperationName, shortOperationName)
                .AddTag("gen_ai.live.operation", GenAI.OperationNameValues.GenerateContent);

            if (_modelId is not null)
            {
                activity.AddTag(GenAI.AttributeGenAiRequestModel, _modelId);
            }

            if (_providerName is not null)
            {
                activity.AddTag(GenAI.AttributeGenAiProviderName, _providerName);
            }

            if (_serverAddress is not null)
            {
                activity
                    .AddTag(Server.Address, _serverAddress)
                    .AddTag(Server.Port, _serverPort);
            }

            if (SessionId is not null)
            {
                activity.AddTag(GenAI.AttributeGenAiConversationId, SessionId);
                activity.AddTag(GenAI.Realtime.SessionId, SessionId);
            }
        }

        return activity;
    }

    private void CompleteActivity(
        Activity? activity,
        Exception? error,
        Stopwatch? stopwatch,
        string? requestModelId,
        ChatResponse? response)
    {
        if (_operationDurationHistogram.Enabled && stopwatch is not null)
        {
            TagList tags = default;
            AddMetricTags(ref tags, requestModelId, response);
            if (error is not null)
            {
                tags.Add(Error.AttributeErrorType, error.GetType().FullName);
            }
            _operationDurationHistogram.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }

        if (_tokenUsageHistogram.Enabled && response?.Usage is { } usage)
        {
            if (usage.InputTokenCount is long inputTokens)
            {
                TagList tags = default;
                tags.Add(GenAI.AttributeGenAiTokenType, GenAI.TokenTypeInput);
                AddMetricTags(ref tags, requestModelId, response);
                _tokenUsageHistogram.Record((int)inputTokens, tags);
            }

            if (usage.OutputTokenCount is long outputTokens)
            {
                TagList tags = default;
                tags.Add(GenAI.AttributeGenAiTokenType, GenAI.TokenTypeOutput);
                AddMetricTags(ref tags, requestModelId, response);
                _tokenUsageHistogram.Record((int)outputTokens, tags);
            }
        }

        if (activity is { IsAllDataRequested: true })
        {
            if (error is not null)
            {
                activity
                    .AddTag(Error.AttributeErrorType, error.GetType().FullName)
                    .SetStatus(ActivityStatusCode.Error, error.Message);
            }

            if (response is not null)
            {
                if (response.FinishReason is ChatFinishReason finishReason)
                {
#pragma warning disable CA1308
                    activity.AddTag(GenAI.AttributeGenAiResponseFinishReasons, $"[\"{finishReason.Value.ToLowerInvariant()}\"]");
#pragma warning restore CA1308
                }

                if (!string.IsNullOrWhiteSpace(response.ResponseId))
                {
                    activity.AddTag(GenAI.AttributeGenAiResponseId, response.ResponseId);
                }

                if (response.ModelId is not null)
                {
                    activity.AddTag(GenAI.AttributeGenAiResponseModel, response.ModelId);
                }

                if (response.Usage?.InputTokenCount is long inputTokens)
                {
                    activity.AddTag(GenAI.AttributeGenAiUsageInputTokens, (int)inputTokens);
                }

                if (response.Usage?.OutputTokenCount is long outputTokens)
                {
                    activity.AddTag(GenAI.AttributeGenAiUsageOutputTokens, (int)outputTokens);
                }

                if (EnableSensitiveData)
                {
                    AddOutputMessagesTags(response, activity);

                    if (response.AdditionalProperties is { } props)
                    {
                        foreach (var kv in props)
                        {
                            activity.AddTag(kv.Key, kv.Value);
                        }
                    }
                }
            }
        }
    }

    private void AddMetricTags(ref TagList tags, string? requestModelId, ChatResponse? response)
    {
        tags.Add(GenAI.AttributeGenAiOperationName, GenAI.OperationNameValues.GenerateContent);

        if (requestModelId is not null)
        {
            tags.Add(GenAI.AttributeGenAiRequestModel, requestModelId);
        }

        if (_providerName is not null)
        {
            tags.Add(GenAI.AttributeGenAiProviderName, _providerName);
        }

        if (_serverAddress is string address)
        {
            tags.Add(Server.Address, address);
            tags.Add(Server.Port, _serverPort);
        }

        if (response?.ModelId is string responseModel)
        {
            tags.Add(GenAI.AttributeGenAiResponseModel, responseModel);
        }
    }

    private void AddInputMessagesTags(IEnumerable<ChatMessage> messages, Activity? activity)
    {
        if (EnableSensitiveData && activity is { IsAllDataRequested: true })
        {
            activity.AddTag(GenAI.AttributeGenAiInputMessages,
                SerializeChatMessages(messages));
        }
    }

    private void AddOutputMessagesTags(ChatResponse response, Activity? activity)
    {
        if (EnableSensitiveData && activity is { IsAllDataRequested: true })
        {
            activity.AddTag(GenAI.AttributeGenAiOutputMessages,
                SerializeChatMessages(response.Messages, response.FinishReason));
        }
    }

    internal static string SerializeChatMessages(IEnumerable<ChatMessage> messages, ChatFinishReason? chatFinishReason = null)
    {
        List<object> output = [];

        string? finishReason =
            chatFinishReason?.Value is null ? null :
            chatFinishReason == ChatFinishReason.Length ? "length" :
            chatFinishReason == ChatFinishReason.ContentFilter ? "content_filter" :
            chatFinishReason == ChatFinishReason.ToolCalls ? "tool_call" :
            "stop";

        foreach (ChatMessage message in messages)
        {
            OtelMessage m = new()
            {
                FinishReason = finishReason,
                Role =
                    message.Role == ChatRole.Assistant ? "assistant" :
                    message.Role == ChatRole.Tool ? "tool" :
                    message.Role == ChatRole.System || message.Role == new ChatRole("developer") ? "system" :
                    "user",
            };

            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    // These are all specified in the convention:

                    case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                        m.Parts.Add(new OtelGenericPart { Content = tc.Text });
                        break;

                    case FunctionCallContent fcc:
                        m.Parts.Add(new OtelToolCallRequestPart
                        {
                            Id = fcc.CallId,
                            Name = fcc.Name,
                            Arguments = fcc.Arguments,
                        });
                        break;

                    case FunctionResultContent frc:
                        m.Parts.Add(new OtelToolCallResponsePart
                        {
                            Id = frc.CallId,
                            Response = frc.Result,
                        });
                        break;

                    // These are non-standard and are using the "generic" non-text part that provides an extensibility mechanism:

                    case TextReasoningContent trc when !string.IsNullOrWhiteSpace(trc.Text):
                        m.Parts.Add(new OtelGenericPart { Type = "reasoning", Content = trc.Text });
                        break;

                    case UriContent uc:
                        m.Parts.Add(new OtelGenericPart { Type = "image", Content = uc.Uri.ToString() });
                        break;

                    case DataContent dc:
                        m.Parts.Add(new OtelGenericPart { Type = "image", Content = dc.Uri });
                        break;

                    case HostedFileContent fc:
                        m.Parts.Add(new OtelGenericPart { Type = "file", Content = fc.FileId });
                        break;

                    case HostedVectorStoreContent vsc:
                        m.Parts.Add(new OtelGenericPart { Type = "vector_store", Content = vsc.VectorStoreId });
                        break;

                    case ErrorContent ec:
                        m.Parts.Add(new OtelGenericPart { Type = "error", Content = ec.Message });
                        break;

                    default:
                        m.Parts.Add(new OtelGenericPart
                        {
                            Type = content.GetType().FullName!,
                            Content = content,
                        });
                        break;
                }
            }

            output.Add(m);
        }

        return JsonSerializer.Serialize(output, defaultOptions.GetTypeInfo(typeof(IList<object>)));
    }

    private sealed class OtelMessage
    {
        public string? Role { get; set; }
        public List<object> Parts { get; set; } = [];
        public string? FinishReason { get; set; }
    }

    private sealed class OtelGenericPart
    {
        public string Type { get; set; } = "text";
        public object? Content { get; set; } // should be a string when Type == "text"
    }

    private sealed class OtelToolCallRequestPart
    {
        public string Type { get; set; } = "tool_call";
        public string? Id { get; set; }
        public string? Name { get; set; }
        public IDictionary<string, object?>? Arguments { get; set; }
    }

    private sealed class OtelToolCallResponsePart
    {
        public string Type { get; set; } = "tool_call_response";
        public string? Id { get; set; }
        public object? Response { get; set; }
    }

    private sealed class OtelFunction
    {
        public string Type { get; set; } = "function";
        public string? Name { get; set; }
        public string? Description { get; set; }
        public JsonElement Parameters { get; set; }
    }
    private static readonly JsonSerializerOptions defaultOptions = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new(OtelContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.TypeInfoResolverChain.Add(ExtensionsAIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.MakeReadOnly();

        return options;
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IList<object>))]
    [JsonSerializable(typeof(OtelMessage))]
    [JsonSerializable(typeof(OtelGenericPart))]
    [JsonSerializable(typeof(OtelToolCallRequestPart))]
    [JsonSerializable(typeof(OtelToolCallResponsePart))]
    [JsonSerializable(typeof(IEnumerable<OtelFunction>))]
    private sealed partial class OtelContext : JsonSerializerContext;
    #endregion

}

