using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Audio;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Primary intent-recognition processor for the IVR pipeline. Owns audio preprocessing
/// (via <see cref="ISpeechRecognizer"/>) and chat-based classification (via <see cref="IChatClient"/>),
/// and locally dispatches a registered <see cref="AITool"/> when an intent is recognized.
/// </summary>
/// <remarks>
/// <para>
/// The backing <see cref="IChatClient"/> is assumed to be a small instruct-style model
/// (for example <c>phi-4-mini-instruct</c> on Azure Foundry) that <b>cannot</b> invoke
/// tools itself. The agent therefore restricts the model to emitting a single JSON
/// intent envelope and performs tool dispatch in-process via
/// <see cref="IvrIntentAgentOptions.DefaultIntentToolMap"/> (preferred) or by matching
/// the recognized intent name against <see cref="AITool.Name"/>.
/// </para>
/// <para>
/// The agent exposes two surfaces:
/// <list type="bullet">
///   <item>The standard <see cref="AIAgent"/> request/response API. <see cref="RunCoreAsync"/>
///         pulls text from user messages, transcribes any inline <see cref="DataContent"/>
///         audio through the speech recognizer, classifies once, and optionally dispatches
///         a tool. The response surfaces the JSON intent payload plus a human-readable summary.</item>
///   <item><see cref="ClassifyAudioStreamAsync(IAsyncEnumerable{ReadOnlyMemory{byte}}, Func{IvrIntentClassificationContext}, CancellationToken)"/>
///         pumps audio frames into the recognizer and emits one <see cref="IvrIntentEvent"/>
///         per final transcript. The classification context is resolved per-utterance so
///         callers can change the candidate set and tool catalog as the IVR workflow progresses.</item>
/// </list>
/// </para>
/// <para>
/// Because <see cref="ClassifyAudioStreamAsync(IAsyncEnumerable{ReadOnlyMemory{byte}}, Func{IvrIntentClassificationContext}, CancellationToken)"/>
/// drives the recognizer it was constructed with, the agent must be scoped per call (or
/// the recognizer must be a per-call instance). DI registration takes care of this for
/// the contact-center container.
/// </para>
/// </remarks>
public sealed class IvrIntentAgent : AIAgent
{
    private static readonly AIAgentMetadata metadata = new("intent-classifier");
    private static readonly ActivitySource activitySource =
        new("Agents.AI.ContactCenter.IvrIntentAgent");
    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IChatClient _chatClient;
    private readonly ISpeechRecognizer? _speechRecognizer;
    private readonly IvrIntentAgentOptions _options;
    private readonly ILogger<IvrIntentAgent> _logger;

    /// <summary>
    /// Creates an intent agent backed by the supplied chat client.
    /// </summary>
    /// <param name="chatClient">SLM chat client used to classify utterances into a JSON intent envelope.</param>
    /// <param name="speechRecognizer">
    /// Optional speech recognizer. Required when audio is supplied either inline on a
    /// <see cref="ChatMessage"/> (<see cref="DataContent"/> with an <c>audio/*</c> media
    /// type) or through <see cref="ClassifyAudioStreamAsync(IAsyncEnumerable{ReadOnlyMemory{byte}}, Func{IvrIntentClassificationContext}, CancellationToken)"/>;
    /// text-only callers may omit it.
    /// </param>
    /// <param name="options">Optional agent configuration (display name, defaults, tool catalog, …).</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public IvrIntentAgent(
        IChatClient chatClient,
        ISpeechRecognizer? speechRecognizer = null,
        IvrIntentAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _speechRecognizer = speechRecognizer;
        _options = options ?? new IvrIntentAgentOptions();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<IvrIntentAgent>();
    }

    /// <summary>
    /// DI-friendly constructor. When this agent is registered as a keyed service, the
    /// container injects the registration key via <paramref name="serviceKey"/> and this
    /// constructor resolves the matching keyed <see cref="IChatClient"/>. When the agent
    /// is registered without a key, <paramref name="serviceKey"/> is <see langword="null"/>
    /// and the default (non-keyed) <see cref="IChatClient"/> is resolved instead.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve the chat client.</param>
    /// <param name="serviceKey">
    /// The key this agent was registered under, or <see langword="null"/> when registered
    /// as a non-keyed service. Supplied automatically by the DI container.
    /// </param>
    /// <param name="speechRecognizer">Optional speech recognizer (see other constructor).</param>
    /// <param name="options">Optional agent configuration.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public IvrIntentAgent(
        IServiceProvider serviceProvider,
        [ServiceKey] object? serviceKey = null,
        ISpeechRecognizer? speechRecognizer = null,
        IvrIntentAgentOptions? options = null,
        ILoggerFactory? loggerFactory = null)
        : this(
            ResolveChatClient(serviceProvider, serviceKey),
            speechRecognizer,
            options,
            loggerFactory)
    {
    }

    private static IChatClient ResolveChatClient(IServiceProvider serviceProvider, object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceKey is null
            ? serviceProvider.GetRequiredService<IChatClient>()
            : serviceProvider.GetRequiredKeyedService<IChatClient>(serviceKey);
    }

    /// <inheritdoc/>
    protected override string? IdCore => _options.Id;

    /// <inheritdoc/>
    public override string? Name => _options.Name;

    /// <inheritdoc/>
    public override string? Description => _options.Description;

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (serviceKey is null)
        {
            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }
            if (serviceType == typeof(AIAgentMetadata))
            {
                return metadata;
            }
            if (serviceType == typeof(IChatClient))
            {
                return _chatClient;
            }
        }
        return base.GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Classify a single utterance against the supplied candidate intents. Convenience
    /// wrapper that bypasses tool dispatch; callers driving the agent through
    /// <see cref="AIAgent.RunAsync"/> get tool dispatch automatically.
    /// </summary>
    public ValueTask<IntentResult> ClassifyAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
        => ClassifyCoreAsync(utterance, validIntents, cancellationToken);

    /// <summary>
    /// Streams audio into the agent's <see cref="ISpeechRecognizer"/> and yields one
    /// <see cref="IvrIntentEvent"/> for every final transcript segment classified against
    /// the candidate set returned by <paramref name="contextProvider"/>. When the resolved
    /// context exposes tools, the matched tool (if any) is invoked locally and its outcome
    /// is attached to the emitted event.
    /// </summary>
    /// <param name="audioFrames">Caller audio frames (raw PCM, recognizer-defined format).</param>
    /// <param name="contextProvider">
    /// Resolves the per-utterance classification context (valid intents, tools, intent-tool map).
    /// Invoked once per final transcript so callers can vary the candidate set as the IVR
    /// workflow progresses.
    /// </param>
    /// <param name="cancellationToken">Cancels both audio ingestion and classification.</param>
    public IAsyncEnumerable<IvrIntentEvent> ClassifyAudioStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        Func<IvrIntentClassificationContext> contextProvider,
        CancellationToken cancellationToken = default)
        => ClassifyAudioStreamCoreAsync(audioFrames, contextProvider, cancellationToken);

    /// <summary>
    /// Convenience overload that classifies a streaming audio source against a fixed
    /// candidate set with no tool dispatch.
    /// </summary>
    public IAsyncEnumerable<IvrIntentEvent> ClassifyAudioStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken = default)
    {
        if (validIntents is null) { throw new ArgumentNullException(nameof(validIntents)); }
        var snapshot = new IvrIntentClassificationContext(
            Utterance: string.Empty,
            ValidIntents: validIntents,
            Tools: Array.Empty<AITool>(),
            IntentToolMap: null);
        return ClassifyAudioStreamCoreAsync(audioFrames, () => snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var materialized = messages as IReadOnlyCollection<ChatMessage> ?? new List<ChatMessage>(messages);

        var utterance = await ExtractUtteranceAsync(materialized, cancellationToken).ConfigureAwait(false);
        var validIntents = ResolveValidIntents(options);
        var tools = ResolveTools(options);
        var intentToolMap = ResolveIntentToolMap(options);

        if (string.IsNullOrWhiteSpace(utterance) || validIntents.Count == 0)
        {
            return BuildResponse(IntentResult.None, utterance, validIntents, toolInvocation: null);
        }

        var result = await ClassifyCoreAsync(utterance, validIntents, cancellationToken)
            .ConfigureAwait(false);

        IvrIntentToolInvocation? toolInvocation = null;
        if (!result.IsNone && tools.Count > 0)
        {
            toolInvocation = await TryInvokeToolAsync(result, tools, intentToolMap, cancellationToken)
                .ConfigureAwait(false);
        }

        return BuildResponse(result, utterance, validIntents, toolInvocation);
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Throw.IfNullOrEmpty(messages);

        var response = await RunCoreAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            yield return new AgentResponseUpdate
            {
                AuthorName = Name,
                AgentId = Id,
                Role = message.Role,
                Contents = message.Contents,
                RawRepresentation = message.RawRepresentation,
            };
        }
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new IvrIntentAgentSession());

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(JsonSerializer.SerializeToElement<object?>(null, jsonSerializerOptions ?? serializerOptions));

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new IvrIntentAgentSession());

    private IReadOnlyList<string> ResolveValidIntents(AgentRunOptions? options)
    {
        if (options is IvrIntentRunOptions ivrOptions &&
            ivrOptions.ValidIntents is { Count: > 0 } supplied)
        {
            return supplied;
        }
        return _options.DefaultIntents ?? Array.Empty<string>();
    }

    private IReadOnlyList<AITool> ResolveTools(AgentRunOptions? options)
    {
        if (options is IvrIntentRunOptions ivrOptions &&
            ivrOptions.Tools is { Count: > 0 } supplied)
        {
            return supplied;
        }
        return _options.DefaultTools ?? Array.Empty<AITool>();
    }

    private IReadOnlyDictionary<string, string>? ResolveIntentToolMap(AgentRunOptions? options)
    {
        if (options is IvrIntentRunOptions ivrOptions &&
            ivrOptions.IntentToolMap is { Count: > 0 } supplied)
        {
            return supplied;
        }
        return _options.DefaultIntentToolMap;
    }

    private async ValueTask<string> ExtractUtteranceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var text = message.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                AppendUtterance(builder, text);
            }

            // Transcribe any inline audio attachments through the recognizer so callers
            // can hand the agent a single ChatMessage with a DataContent audio blob.
            foreach (var content in message.Contents)
            {
                if (content is DataContent data && IsAudio(data.MediaType))
                {
                    var transcribed = await TranscribeAudioAsync(data.Data, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(transcribed))
                    {
                        AppendUtterance(builder, transcribed);
                    }
                }
            }
        }
        return builder.ToString();
    }

    private static void AppendUtterance(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }
        builder.Append(text);
    }

    private static bool IsAudio(string? mediaType)
        => mediaType is { Length: > 0 } && mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private async Task<string> TranscribeAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        if (_speechRecognizer is null)
        {
            _logger.LogWarning(
                "Received inline audio content but no ISpeechRecognizer is configured; ignoring audio bytes.");
            return string.Empty;
        }
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var collectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var collectTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var segment in _speechRecognizer
                    .GetTranscriptsAsync(collectCts.Token)
                    .ConfigureAwait(false))
                {
                    if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                    {
                        continue;
                    }
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }
                    builder.Append(segment.Text);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }, CancellationToken.None);

        try
        {
            await _speechRecognizer.WriteAudioAsync(audio, cancellationToken).ConfigureAwait(false);
            await _speechRecognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Give the recognizer a brief grace period to drain finals, then stop the collector.
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown */ }
            await collectCts.CancelAsync().ConfigureAwait(false);
            try { await collectTask.ConfigureAwait(false); } catch { /* shutdown */ }
            collectCts.Dispose();
        }

        return builder.ToString();
    }

    private AgentResponse BuildResponse(
        IntentResult result,
        string utterance,
        IReadOnlyList<string> candidates,
        IvrIntentToolInvocation? toolInvocation)
    {
        var payload = new IntentResponsePayload(
            Intent: result.IntentName,
            Confidence: result.Confidence,
            Entities: result.Entities,
            Utterance: utterance,
            Candidates: candidates,
            Tool: toolInvocation is null
                ? null
                : new ToolInvocationPayload(
                    toolInvocation.ToolName,
                    toolInvocation.Result?.ToString(),
                    toolInvocation.Error?.Message));

        var json = JsonSerializer.Serialize(payload, serializerOptions);
        var summary = result.IsNone
            ? $"No intent matched for utterance: \"{utterance}\""
            : toolInvocation is null
                ? $"Intent '{result.IntentName}' (confidence {result.Confidence:F2})"
                : toolInvocation.Error is null
                    ? $"Intent '{result.IntentName}' (confidence {result.Confidence:F2}) → invoked tool '{toolInvocation.ToolName}'"
                    : $"Intent '{result.IntentName}' (confidence {result.Confidence:F2}) → tool '{toolInvocation.ToolName}' failed: {toolInvocation.Error.Message}";

        var responseMessage = new ChatMessage(ChatRole.Assistant, new[]
        {
            (AIContent)new TextContent(summary),
            new DataContent(json, "application/json"),
        })
        {
            AuthorName = Name,
        };

        return new AgentResponse(responseMessage)
        {
            AgentId = Id,
        };
    }

    private async IAsyncEnumerable<IvrIntentEvent> ClassifyAudioStreamCoreAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        Func<IvrIntentClassificationContext> contextProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (audioFrames is null) { throw new ArgumentNullException(nameof(audioFrames)); }
        if (contextProvider is null) { throw new ArgumentNullException(nameof(contextProvider)); }
        if (_speechRecognizer is null)
        {
            throw new InvalidOperationException(
                "IvrIntentAgent.ClassifyAudioStreamAsync requires an ISpeechRecognizer; " +
                "configure one through DI or the agent constructor.");
        }

        var events = Channel.CreateUnbounded<IvrIntentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var audioTask = PumpAudioAsync(audioFrames, pumpCts.Token);
        var classifyTask = ClassifyTranscriptsAsync(contextProvider, events.Writer, pumpCts.Token);

        try
        {
            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (events.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try { await audioTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio pump task faulted during shutdown");
            }
            try { await classifyTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex)
            {
                _logger.LogDebug(ex, "Classification task faulted during shutdown");
            }
            pumpCts.Dispose();
        }
    }

    private async Task PumpAudioAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in audioFrames.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (frame.IsEmpty)
                {
                    continue;
                }
                await _speechRecognizer!.WriteAudioAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _speechRecognizer!.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Recognizer.CompleteAsync threw during audio pump shutdown");
            }
        }
    }

    private async Task ClassifyTranscriptsAsync(
        Func<IvrIntentClassificationContext> contextProvider,
        ChannelWriter<IvrIntentEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _speechRecognizer!
                .GetTranscriptsAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                IvrIntentClassificationContext context;
                try
                {
                    context = contextProvider();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Intent classification context provider threw; skipping transcript segment");
                    continue;
                }

                if (context.ValidIntents.Count == 0)
                {
                    await writer.WriteAsync(
                        new IvrIntentEvent(segment, IntentResult.None, DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                IntentResult result;
                try
                {
                    result = await ClassifyCoreAsync(segment.Text, context.ValidIntents, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Intent classification failed for transcript segment; emitting IntentResult.None");
                    result = IntentResult.None;
                }

                IvrIntentToolInvocation? toolInvocation = null;
                if (!result.IsNone && context.Tools.Count > 0)
                {
                    toolInvocation = await TryInvokeToolAsync(
                        result, context.Tools, context.IntentToolMap, cancellationToken)
                        .ConfigureAwait(false);
                }

                await writer.WriteAsync(
                    new IvrIntentEvent(segment, result, DateTimeOffset.UtcNow, toolInvocation),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async ValueTask<IntentResult> ClassifyCoreAsync(
        string utterance,
        IReadOnlyList<string> validIntents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(utterance) || validIntents is null || validIntents.Count == 0)
        {
            return IntentResult.None;
        }

        using var activity = activitySource.StartActivity("intent.classify.chatclient");
        activity?.SetTag("intent.candidate_count", validIntents.Count);
        activity?.SetTag("intent.utterance_length", utterance.Length);

        var messages = BuildClassificationMessages(utterance, validIntents);
        var chatOptions = BuildChatOptions();

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex, "Chat-client intent classification failed; returning IntentResult.None");
            return IntentResult.None;
        }

        var raw = response.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            activity?.SetTag("intent.result", "none");
            activity?.SetTag("intent.failure_reason", "empty_response");
            return IntentResult.None;
        }

        if (!TryParseJsonObject(raw, out var doc))
        {
            activity?.SetTag("intent.result", "none");
            activity?.SetTag("intent.failure_reason", "unparseable_response");
            _logger.LogDebug("Intent classifier received unparseable response: {Raw}", raw);
            return IntentResult.None;
        }

        using (doc)
        {
            var result = ProjectResult(doc.RootElement, validIntents);
            activity?.SetTag("intent.result", result.IntentName ?? "none");
            activity?.SetTag("intent.confidence", result.Confidence);
            return result;
        }
    }

    private List<ChatMessage> BuildClassificationMessages(string utterance, IReadOnlyList<string> validIntents)
    {
        var userPrompt = new StringBuilder();
        userPrompt.Append("Language: ").AppendLine(_options.Language);
        userPrompt.AppendLine("Candidate intents:");
        for (var i = 0; i < validIntents.Count; i++)
        {
            userPrompt.Append("- ").AppendLine(validIntents[i]);
        }
        userPrompt.AppendLine("- none");

        var examples = _options.IntentExamples;
        if (examples is { Count: > 0 })
        {
            var wroteHeader = false;
            for (var i = 0; i < validIntents.Count; i++)
            {
                var name = validIntents[i];
                if (!examples.TryGetValue(name, out var samples) || samples is null || samples.Count == 0)
                {
                    continue;
                }

                if (!wroteHeader)
                {
                    userPrompt.AppendLine();
                    userPrompt.AppendLine("Examples:");
                    wroteHeader = true;
                }

                foreach (var sample in samples)
                {
                    if (string.IsNullOrWhiteSpace(sample))
                    {
                        continue;
                    }
                    userPrompt.Append("- intent=").Append(name).Append(" → \"").Append(sample).Append("\"\n");
                }
            }
        }

        userPrompt.AppendLine();
        userPrompt.Append("Utterance: \"").Append(utterance).Append('"');

        return new List<ChatMessage>
        {
            new(ChatRole.System, _options.SystemPrompt),
            new(ChatRole.User, userPrompt.ToString()),
        };
    }

    private ChatOptions BuildChatOptions()
    {
        var chatOptions = new ChatOptions();
        if (_options.Temperature is { } temperature)
        {
            chatOptions.Temperature = temperature;
        }
        if (_options.MaxOutputTokens is { } maxTokens)
        {
            chatOptions.MaxOutputTokens = maxTokens;
        }
        if (_options.RequestJsonResponseFormat)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.Json;
        }
        return chatOptions;
    }

    private static bool TryParseJsonObject(string raw, out JsonDocument document)
    {
        var span = raw.AsSpan();
        var start = span.IndexOf('{');
        if (start < 0)
        {
            document = null!;
            return false;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < span.Length; i++)
        {
            var c = span[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString)
            {
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var slice = span.Slice(start, i - start + 1);
                    try
                    {
                        document = JsonDocument.Parse(slice.ToString());
                        return true;
                    }
                    catch (JsonException)
                    {
                        document = null!;
                        return false;
                    }
                }
            }
        }

        document = null!;
        return false;
    }

    private IntentResult ProjectResult(JsonElement root, IReadOnlyList<string> validIntents)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return IntentResult.None;
        }

        string? intentName = null;
        if (root.TryGetProperty("intent", out var intentElement))
        {
            intentName = intentElement.ValueKind switch
            {
                JsonValueKind.String => intentElement.GetString(),
                JsonValueKind.Null => null,
                _ => intentElement.ToString(),
            };
        }
        else if (root.TryGetProperty("intent_name", out var altName))
        {
            intentName = altName.GetString();
        }
        else if (root.TryGetProperty("name", out var nameElement))
        {
            intentName = nameElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(intentName) ||
            string.Equals(intentName, "none", StringComparison.OrdinalIgnoreCase))
        {
            return IntentResult.None;
        }

        string? canonical = null;
        for (var i = 0; i < validIntents.Count; i++)
        {
            if (string.Equals(validIntents[i], intentName, StringComparison.OrdinalIgnoreCase))
            {
                canonical = validIntents[i];
                break;
            }
        }
        if (canonical is null)
        {
            _logger.LogDebug(
                "Intent classifier returned out-of-set intent '{Intent}'; coercing to none", intentName);
            return IntentResult.None;
        }

        var confidence = 0.0;
        if (root.TryGetProperty("confidence", out var confidenceElement))
        {
            confidence = confidenceElement.ValueKind switch
            {
                JsonValueKind.Number => confidenceElement.TryGetDouble(out var d) ? d : 0.0,
                JsonValueKind.String =>
                    double.TryParse(confidenceElement.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.0,
                _ => 0.0,
            };
        }
        else
        {
            confidence = 1.0;
        }

        if (confidence < 0.0) { confidence = 0.0; }
        else if (confidence > 1.0) { confidence = 1.0; }

        if (confidence < _options.MinimumConfidence)
        {
            return IntentResult.None;
        }

        IReadOnlyDictionary<string, string>? entities = null;
        if (root.TryGetProperty("entities", out var entitiesElement) &&
            entitiesElement.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, string>? map = null;
            foreach (var property in entitiesElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
                if (value is null)
                {
                    continue;
                }
                map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                map[property.Name] = value;
            }
            if (map is { Count: > 0 })
            {
                entities = map;
            }
        }

        return new IntentResult
        {
            IntentName = canonical,
            Confidence = confidence,
            Entities = entities,
        };
    }

    private async ValueTask<IvrIntentToolInvocation?> TryInvokeToolAsync(
        IntentResult result,
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, string>? intentToolMap,
        CancellationToken cancellationToken)
    {
        if (result.IntentName is not { Length: > 0 } intent || tools.Count == 0)
        {
            return null;
        }

        var tool = ResolveTool(intent, tools, intentToolMap);
        if (tool is null)
        {
            _logger.LogDebug(
                "No tool resolved for intent '{Intent}'; tools=[{Tools}], explicitMap={HasMap}",
                intent, string.Join(",", ToolNames(tools)), intentToolMap is { Count: > 0 });
            return null;
        }

        if (tool is not AIFunction function)
        {
            _logger.LogDebug(
                "Resolved tool '{Tool}' for intent '{Intent}' is not an AIFunction; skipping invocation",
                tool.Name, intent);
            return null;
        }

        var arguments = new AIFunctionArguments();
        if (result.Entities is { Count: > 0 })
        {
            foreach (var (k, v) in result.Entities)
            {
                arguments[k] = v;
            }
        }

        try
        {
            var invocationResult = await function.InvokeAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            return new IvrIntentToolInvocation(function.Name, invocationResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tool '{Tool}' invocation failed for intent '{Intent}'", function.Name, intent);
            return new IvrIntentToolInvocation(function.Name, Result: null, Error: ex);
        }
    }

    private static AITool? ResolveTool(
        string intent,
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, string>? intentToolMap)
    {
        // 1. Explicit intent -> tool-name map wins.
        if (intentToolMap is { Count: > 0 } &&
            intentToolMap.TryGetValue(intent, out var mappedName) &&
            !string.IsNullOrWhiteSpace(mappedName))
        {
            for (var i = 0; i < tools.Count; i++)
            {
                if (string.Equals(tools[i].Name, mappedName, StringComparison.OrdinalIgnoreCase))
                {
                    return tools[i];
                }
            }
        }

        // 2. Fall back to matching the intent name against the tool name.
        for (var i = 0; i < tools.Count; i++)
        {
            if (string.Equals(tools[i].Name, intent, StringComparison.OrdinalIgnoreCase))
            {
                return tools[i];
            }
        }

        return null;
    }

    private static IEnumerable<string> ToolNames(IReadOnlyList<AITool> tools)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            yield return tools[i].Name;
        }
    }

    private sealed record IntentResponsePayload(
        string? Intent,
        double Confidence,
        IReadOnlyDictionary<string, string>? Entities,
        string Utterance,
        IReadOnlyList<string> Candidates,
        ToolInvocationPayload? Tool);

    private sealed record ToolInvocationPayload(
        string ToolName,
        string? Result,
        string? Error);

    private sealed class IvrIntentAgentSession : AgentSession;
}
