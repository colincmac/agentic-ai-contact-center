
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.Extensions.AGUI;

/// <summary>
/// Provides extension methods for mapping AG-UI agents to ASP.NET Core endpoints.
/// </summary>
public static class AGUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an AG-UI agent endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the endpoint.</param>
    /// <param name="aiAgent">The agent instance.</param>
    /// <returns>An <see cref="IEndpointConventionBuilder"/> for the mapped endpoint.</returns>
    public static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("route")] string pattern,
        AIAgent aiAgent)
    {
        return endpoints.MapPost(pattern, async ([FromBody] RunAgentInput? input, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            // Normalize assistant/tool ordering before we map to ChatMessage
            var aguiMessages = input.Messages?.ToList() ?? new List<AGUIMessage>();
            FixToolMessageOrdering(aguiMessages);

            var messages = aguiMessages.AsChatMessages(jsonSerializerOptions);
            var clientTools = input.Tools?.AsAITools().ToList();

            // Create run options with AG-UI context in AdditionalProperties
            var runOptions = new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = clientTools,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["ag_ui_state"] = input.State,
                        ["ag_ui_context"] = input.Context?.Select(c => new KeyValuePair<string, string>(c.Description, c.Value)).ToArray(),
                        ["ag_ui_forwarded_properties"] = input.ForwardedProperties,
                        ["ag_ui_thread_id"] = input.ThreadId,
                        ["ag_ui_run_id"] = input.RunId
                    }
                }
            };

            // Run the agent and convert to AG-UI events
            var events = aiAgent.RunStreamingAsync(
                messages,
                options: runOptions,
                cancellationToken: cancellationToken)
                .AsChatResponseUpdatesAsync()
                .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
                .AsAGUIEventStreamAsync(
                    input.ThreadId,
                    input.RunId,
                    jsonSerializerOptions,
                    cancellationToken);

            var sseLogger = context.RequestServices.GetRequiredService<ILogger<AGUIServerSentEventsResult>>();
            return new AGUIServerSentEventsResult(events, sseLogger);
        });
    }
    public static async IAsyncEnumerable<ChatResponseUpdate> FilterServerToolsFromMixedToolInvocationsAsync(
        this IAsyncEnumerable<ChatResponseUpdate> updates,
        List<AITool>? clientTools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (clientTools is null || clientTools.Count == 0)
        {
            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        var set = new HashSet<string>(clientTools.Count);
        foreach (var tool in clientTools)
        {
            set.Add(tool.Name);
        }

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (update.FinishReason == ChatFinishReason.ToolCalls)
            {
                var containsClientTools = false;
                var containsServerTools = false;
                for (var i = update.Contents.Count - 1; i >= 0; i--)
                {
                    var content = update.Contents[i];
                    if (content is FunctionCallContent functionCallContent)
                    {
                        containsClientTools |= set.Contains(functionCallContent.Name);
                        containsServerTools |= !set.Contains(functionCallContent.Name);
                        if (containsClientTools && containsServerTools)
                        {
                            break;
                        }
                    }
                }

                if (containsClientTools && containsServerTools)
                {
                    var newContents = new List<AIContent>();
                    for (var i = update.Contents.Count - 1; i >= 0; i--)
                    {
                        var content = update.Contents[i];
                        if (content is not FunctionCallContent fcc ||
                            set.Contains(fcc.Name))
                        {
                            newContents.Add(content);
                        }
                    }

                    yield return new ChatResponseUpdate(update.Role, newContents)
                    {
                        ConversationId = update.ConversationId,
                        ResponseId = update.ResponseId,
                        FinishReason = update.FinishReason,
                        AdditionalProperties = update.AdditionalProperties,
                        AuthorName = update.AuthorName,
                        CreatedAt = update.CreatedAt,
                        MessageId = update.MessageId,
                        ModelId = update.ModelId
                    };
                }
                else
                {
                    yield return update;
                }
            }
            else
            {
                yield return update;
            }
        }
    }
    /// <summary>
    /// Ensures that tool result messages appear immediately after their corresponding assistant messages
    /// that contain matching tool call IDs. Any unmatched tool messages are appended at the end.
    /// </summary>
    /// <param name="messages">The AG-UI messages to reorder in-place.</param>
    internal static void FixToolMessageOrdering(List<AGUIMessage> messages)
    {
        if (messages == null || messages.Count == 0)
        {
            return;
        }

        // Collect all tool messages by ToolCallId
        var toolsByCallId = new Dictionary<string, Queue<AGUIToolMessage>>();
        var toolsWithoutCallId = new List<AGUIToolMessage>();

        foreach (var msg in messages)
        {
            if (msg is AGUIToolMessage toolMsg)
            {
                if (!string.IsNullOrWhiteSpace(toolMsg.ToolCallId))
                {
                    if (!toolsByCallId.TryGetValue(toolMsg.ToolCallId, out var queue))
                    {
                        queue = new Queue<AGUIToolMessage>();
                        toolsByCallId[toolMsg.ToolCallId] = queue;
                    }

                    queue.Enqueue(toolMsg);
                }
                else
                {
                    toolsWithoutCallId.Add(toolMsg);
                }
            }
        }

        var reordered = new List<AGUIMessage>(messages.Count);

        foreach (var msg in messages)
        {
            // Reinsert tool messages next to their assistant, so skip them in this pass.
            if (msg is AGUIToolMessage)
            {
                continue;
            }

            reordered.Add(msg);

            if (msg is AGUIAssistantMessage assistant &&
                assistant.ToolCalls is { Length: > 0 })
            {
                // For each tool call in this assistant message, append
                // the corresponding tool result message(s) immediately after.
                foreach (var toolCall in assistant.ToolCalls)
                {
                    if (toolCall?.Id is null)
                    {
                        continue;
                    }

                    if (toolsByCallId.TryGetValue(toolCall.Id, out var queue))
                    {
                        while (queue.Count > 0)
                        {
                            var toolMsg = queue.Dequeue();
                            reordered.Add(toolMsg);
                        }

                        toolsByCallId.Remove(toolCall.Id);
                    }
                }
            }
        }

        // Any remaining tool messages (without matching assistant toolCalls)
        // are appended at the end so nothing is lost.
        foreach (var remainingQueue in toolsByCallId.Values)
        {
            while (remainingQueue.Count > 0)
            {
                reordered.Add(remainingQueue.Dequeue());
            }
        }

        foreach (var toolMsg in toolsWithoutCallId)
        {
            reordered.Add(toolMsg);
        }

        // Replace the original list contents
        messages.Clear();
        foreach (var msg in reordered)
        {
            messages.Add(msg);
        }
    }
}

public sealed partial class AGUIServerSentEventsResult : IResult, IDisposable
{
    private readonly IAsyncEnumerable<BaseEvent> _events;
    private readonly ILogger<AGUIServerSentEventsResult> _logger;
    private Utf8JsonWriter? _jsonWriter;

    public int? StatusCode => StatusCodes.Status200OK;

    internal AGUIServerSentEventsResult(IAsyncEnumerable<BaseEvent> events, ILogger<AGUIServerSentEventsResult> logger)
    {
        this._events = events;
        this._logger = logger;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (httpContext == null)
        {
            throw new ArgumentNullException(nameof(httpContext));
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache,no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

        var body = httpContext.Response.Body;
        var cancellationToken = httpContext.RequestAborted;

        try
        {
            await SseFormatter.WriteAsync(
                WrapEventsAsSseItemsAsync(this._events, cancellationToken),
                body,
                this.SerializeEvent,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStreamingError(this._logger, ex);
            // If an error occurs during streaming, try to send an error event before closing
            try
            {
                var errorEvent = new RunErrorEvent
                {
                    Code = "StreamingError",
                    Message = ex.Message
                };
                await SseFormatter.WriteAsync(
                    WrapEventsAsSseItemsAsync([errorEvent]),
                    body,
                    this.SerializeEvent,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception sendErrorEx)
            {
                // If we can't send the error event, just let the connection close
                LogSendErrorEventFailed(this._logger, sendErrorEx);
            }
        }

        await body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapEventsAsSseItemsAsync(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (BaseEvent evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapEventsAsSseItemsAsync(
        IEnumerable<BaseEvent> events)
    {
        foreach (BaseEvent evt in events)
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }

    private void SerializeEvent(SseItem<BaseEvent> item, IBufferWriter<byte> writer)
    {
        if (this._jsonWriter == null)
        {
            this._jsonWriter = new Utf8JsonWriter(writer);
        }
        else
        {
            this._jsonWriter.Reset(writer);
        }
        JsonSerializer.Serialize(this._jsonWriter, item.Data, AGUIJsonSerializerContext.Default.BaseEvent);
    }

    public void Dispose()
    {
        this._jsonWriter?.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An error occurred while streaming AG-UI events",
        SkipEnabledCheck = true)]
    private static partial void LogStreamingError(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to send error event to client after streaming failure",
        SkipEnabledCheck = true)]
    private static partial void LogSendErrorEventFailed(ILogger logger, Exception exception);
}
