using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agents.AI.Realtime;
using Azure.AI.AgentServer.Invocations;
using Extensions.AI.Contents;
using Extensions.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Agents.AI.Hosting.Realtime;

/// <summary>
/// Example handler that bridges the Azure AI Invocations WebSocket protocol (<c>/invocations_ws</c>) with a
/// <see cref="RealtimeAIAgent"/>, enabling a realtime, full-duplex voice/text agent to be hosted
/// as an Azure Foundry hosted agent.
/// </summary>
/// <remarks>
/// <para>
/// The handler establishes a realtime session on connect, streams the agent's server messages back
/// to the client as JSON frames, and translates inbound client frames into <see cref="RealtimeClientMessage"/>s.
/// Reading and writing happen concurrently — the defining property of the realtime transport.
/// </para>
/// <para>
/// Client → server frames (text JSON, discriminated by <c>type</c>):
/// <list type="bullet">
///   <item><c>{ "type": "session.update", "instructions": "..." }</c> — update session options.</item>
///   <item><c>{ "type": "input_audio.append", "audio": "&lt;base64 PCM&gt;" }</c> — append caller audio.</item>
///   <item><c>{ "type": "input_audio.commit" }</c> — commit the buffered audio for processing.</item>
///   <item><c>{ "type": "input_text", "text": "..." }</c> — inject a user text turn.</item>
///   <item><c>{ "type": "response.create" }</c> — ask the agent to generate a response.</item>
///   <item><c>{ "type": "bye" }</c> — graceful client-initiated shutdown.</item>
/// </list>
/// </para>
/// <para>
/// Server → client frames: <c>ready</c>, <c>response.created</c>, <c>output_audio.delta</c>,
/// <c>output_text.delta</c>, <c>transcript</c>, <c>function_call</c>, <c>vad</c>, <c>response.done</c>,
/// and <c>error</c>.
/// </para>
/// <para>
/// <strong>Security:</strong> realtime output originates from an external AI service and must be treated as
/// untrusted. Validate and sanitize text/audio before rendering or executing it in any sensitive context.
/// </para>
/// </remarks>
public class RealtimeAgentInvocationHandler : InvocationWebSocketHandler
{
    // Client → server frame types.
    private const string SessionUpdate = "session.update";
    private const string InputAudioAppend = "input_audio.append";
    private const string InputAudioCommit = "input_audio.commit";
    private const string InputText = "input_text";
    private const string ResponseCreate = "response.create";
    private const string Bye = "bye";

    // Server → client frame types.
    private const string Ready = "ready";
    private const string ResponseCreated = "response.created";
    private const string ResponseDone = "response.done";
    private const string OutputAudioDelta = "output_audio.delta";
    private const string OutputTextDelta = "output_text.delta";
    private const string Transcript = "transcript";
    private const string FunctionCall = "function_call";
    private const string Vad = "vad";
    private const string Error = "error";

    private const string PcmMediaType = "audio/pcm";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RealtimeAgentInvocationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAgentInvocationHandler"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve the target <see cref="RealtimeAIAgent"/>.</param>
    /// <param name="logger">The logger used for diagnostics.</param>
    public RealtimeAgentInvocationHandler(
        IServiceProvider serviceProvider,
        ILogger<RealtimeAgentInvocationHandler> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // HTTP /invocations — this agent only speaks the WebSocket realtime protocol.
    public override Task HandleAsync(
        HttpRequest request, HttpResponse response,
        InvocationContext context, CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        return response.WriteAsync(
            "This agent only supports the WebSocket invocations protocol (/invocations_ws).",
            cancellationToken);
    }

    // /invocations_ws — true full-duplex realtime streaming.
    public override async Task HandleWebSocketAsync(
        WebSocket webSocket,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // WebSocket.SendAsync is not safe for concurrent writers; the response pump and the
        // read loop both emit frames, so serialize all sends through this gate.
        using var sendLock = new SemaphoreSlim(1, 1);

        RealtimeAIAgent agent;
        try
        {
            agent = ResolveAgent(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve a realtime agent for the WebSocket invocation.");
            await SendFrameAsync(webSocket, sendLock, new { type = Error, message = ex.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        RealtimeAIAgentSession session;
        try
        {
            session = await agent.CreateSessionAsync(cancellationToken: connectionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create a realtime session for agent '{AgentName}'.", agent.Name);
            await SendFrameAsync(webSocket, sendLock, new { type = Error, message = ex.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Start streaming the agent's server messages to the client BEFORE processing any input,
        // so nothing emitted by the realtime backend is missed.
        var pump = PumpAgentResponsesAsync(webSocket, agent, session, sendLock, connectionCts.Token);

        await SendFrameAsync(webSocket, sendLock, new { type = Ready, sessionId = context.SessionId }, connectionCts.Token).ConfigureAwait(false);

        var buffer = new byte[8192];
        try
        {
            while (webSocket.State == WebSocketState.Open && !connectionCts.IsCancellationRequested)
            {
                var message = await ReceiveTextMessageAsync(webSocket, buffer, connectionCts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    // Close frame received.
                    break;
                }
                if (message.Length == 0)
                {
                    continue;
                }

                await DispatchClientMessageAsync(webSocket, agent, session, sendLock, message, connectionCts).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection cancelled — normal shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket closed unexpectedly during realtime invocation.");
        }
        finally
        {
            // Tear down the agent pump and the realtime client session.
            await connectionCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // Pump faults are already logged inside the pump; nothing to recover here.
            }

            if (session.ClientSession is { } clientSession)
            {
                try
                {
                    await clientSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing realtime client session.");
                }
            }
        }
    }

    /// <summary>
    /// Streams server messages produced by the agent and forwards them to the client as JSON frames.
    /// </summary>
    private async Task PumpAgentResponsesAsync(
        WebSocket webSocket,
        RealtimeAIAgent agent,
        RealtimeAIAgentSession session,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in agent.RunStreamingAsync(session, null, cancellationToken).ConfigureAwait(false))
            {
                var role = update.Role;
                foreach (var content in update.Contents)
                {
                    object? frame = content switch
                    {
                        DataContent audio when IsAudio(audio) && !audio.Data.IsEmpty =>
                            new { type = OutputAudioDelta, id = update.ResponseId, audio = Convert.ToBase64String(audio.Data.Span) },

                        AudioTranscriptionContent atc when !string.IsNullOrWhiteSpace(atc.Text) =>
                            new { type = Transcript, speaker = SpeakerOf(role), text = atc.Text, final = false },

                        // Some providers surface a final user transcript as TextContent.
                        TextContent userText when role == ChatRole.User && !string.IsNullOrWhiteSpace(userText.Text) =>
                            new { type = Transcript, speaker = "user", text = userText.Text, final = true },

                        TextContent tc when !string.IsNullOrWhiteSpace(tc.Text) =>
                            new { type = OutputTextDelta, id = update.ResponseId, text = tc.Text },

                        FunctionCallContent fcc =>
                            new { type = FunctionCall, id = update.ResponseId, name = fcc.Name, callId = fcc.CallId, arguments = fcc.Arguments },

                        RealtimeVadContent vad =>
                            new { type = Vad, vadEvent = vad.VadEvent.ToString() },

                        RealtimeResponseStartContent =>
                            new { type = ResponseCreated, id = update.ResponseId },

                        RealtimeResponseFinishedContent =>
                            new { type = ResponseDone, id = update.ResponseId },

                        ErrorContent err =>
                            new { type = Error, id = update.ResponseId, message = err.Message },

                        _ => null,
                    };

                    if (frame is not null)
                    {
                        await SendFrameAsync(webSocket, sendLock, frame, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection cancelled — normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime agent response pump faulted.");
            try
            {
                await SendFrameAsync(webSocket, sendLock, new { type = Error, message = ex.Message }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Socket may already be gone — best-effort error reporting only.
            }
        }
    }

    /// <summary>
    /// Parses a client frame and dispatches it to the agent as the corresponding realtime client message.
    /// </summary>
    private async Task DispatchClientMessageAsync(
        WebSocket webSocket,
        RealtimeAIAgent agent,
        RealtimeAIAgentSession session,
        SemaphoreSlim sendLock,
        string json,
        CancellationTokenSource connectionCts)
    {
        var ct = connectionCts.Token;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await SendFrameAsync(webSocket, sendLock, new { type = Error, message = "invalid JSON payload" }, ct).ConfigureAwait(false);
            return;
        }

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
        {
            await SendFrameAsync(webSocket, sendLock, new { type = Error, message = "missing 'type' field" }, ct).ConfigureAwait(false);
            return;
        }

        switch (typeProp.GetString())
        {
            case SessionUpdate:
            {
                var instructions = root.TryGetProperty("instructions", out var instructionsProp) && instructionsProp.ValueKind == JsonValueKind.String
                    ? instructionsProp.GetString()
                    : null;
                var options = new RealtimeSessionOptions { Instructions = instructions };
                await agent.SendAsync(session, new SessionUpdateRealtimeClientMessage(options), ct).ConfigureAwait(false);
                break;
            }

            case InputAudioAppend:
            {
                if (root.TryGetProperty("audio", out var audioProp)
                    && audioProp.ValueKind == JsonValueKind.String
                    && audioProp.GetString() is { Length: > 0 } base64Audio)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = Convert.FromBase64String(base64Audio);
                    }
                    catch (FormatException)
                    {
                        await SendFrameAsync(webSocket, sendLock, new { type = Error, message = "audio is not valid base64" }, ct).ConfigureAwait(false);
                        break;
                    }
                    await agent.SendAudioAsync(session, new DataContent(bytes, PcmMediaType), ct).ConfigureAwait(false);
                }
                break;
            }

            case InputAudioCommit:
                await agent.SendAsync(session, new InputAudioBufferCommitRealtimeClientMessage(), ct).ConfigureAwait(false);
                break;

            case InputText:
            {
                if (root.TryGetProperty("text", out var textProp)
                    && textProp.ValueKind == JsonValueKind.String
                    && textProp.GetString() is { Length: > 0 } text)
                {
                    await agent.SendAsync(session, new ChatMessage(ChatRole.User, text), ct).ConfigureAwait(false);
                }
                break;
            }

            case ResponseCreate:
                await agent.SendAsync(session, new CreateResponseRealtimeClientMessage(), ct).ConfigureAwait(false);
                break;

            case Bye:
                await connectionCts.CancelAsync().ConfigureAwait(false);
                break;

            default:
                await SendFrameAsync(webSocket, sendLock, new { type = Error, message = $"unknown type: {typeProp.GetString()}" }, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Resolves the target <see cref="RealtimeAIAgent"/>. Honours the <c>agent</c> (or <c>agent_name</c>)
    /// query parameter as a keyed-service lookup, falling back to a non-keyed default registration.
    /// </summary>
    private RealtimeAIAgent ResolveAgent(InvocationContext context)
    {
        var agentName = GetAgentName(context);
        if (!string.IsNullOrEmpty(agentName))
        {
            var keyed = _serviceProvider.GetKeyedService<RealtimeAIAgent>(agentName);
            if (keyed is not null)
            {
                return keyed;
            }

            _logger.LogWarning("Realtime agent '{AgentName}' not found in keyed services. Attempting default resolution.", agentName);
        }

        var defaultAgent = _serviceProvider.GetService<RealtimeAIAgent>();
        if (defaultAgent is not null)
        {
            return defaultAgent;
        }

        throw new InvalidOperationException(
            string.IsNullOrEmpty(agentName)
                ? "No agent name was supplied (via the 'agent' query parameter) and no default RealtimeAIAgent is registered."
                : $"RealtimeAIAgent '{agentName}' was not found and no default RealtimeAIAgent is registered. " +
                  $"Register it via services.AddKeyedSingleton<RealtimeAIAgent>(\"{agentName}\", ...).");
    }

    private static string? GetAgentName(InvocationContext context)
    {
        if (context.QueryParameters.TryGetValue("agent", out var agent) && !StringValues.IsNullOrEmpty(agent))
        {
            return agent.ToString();
        }
        if (context.QueryParameters.TryGetValue("agent_name", out var agentName) && !StringValues.IsNullOrEmpty(agentName))
        {
            return agentName.ToString();
        }
        return null;
    }

    private static bool IsAudio(DataContent content) =>
        content.MediaType is { } mediaType && mediaType.StartsWith("audio", StringComparison.OrdinalIgnoreCase);

    private static string SpeakerOf(ChatRole? role) =>
        role == ChatRole.User ? "user" : "assistant";

    /// <summary>
    /// Reads a complete (possibly fragmented) text message from the socket. Returns <see langword="null"/>
    /// when a Close frame is received, or an empty string when a non-text message is drained.
    /// </summary>
    private static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Text)
            {
                ms.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task SendFrameAsync<T>(WebSocket webSocket, SemaphoreSlim sendLock, T payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }
}
