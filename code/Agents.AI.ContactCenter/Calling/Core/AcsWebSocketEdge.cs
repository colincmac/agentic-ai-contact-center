using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Signaling;
using Agents.AI.ContactCenter.Telemetry;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Streaming variant of the ACS caller edge. Owns the bidirectional media
/// WebSocket and speaks PCM. Pairs with strategies that emit
/// <see cref="OutboundDirective.Audio"/> directly (Realtime, Ensemble, the WS
/// flavour of DTMF). Pair verb-based strategies with
/// <see cref="AcsCallAutomationEdge"/> instead.
/// </summary>
public sealed class AcsWebSocketEdge : ICallEdge, ICallControl
{
    private readonly WebSocket _webSocket;
    private readonly CallConnectionProperties _call;
    private readonly CallAutomationClient _callAutomationClient;
    private readonly ILogger<AcsWebSocketEdge> _logger;
    private readonly CancellationTokenSource _cts;

    private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<DtmfTone> _inboundDtmf = Channel.CreateUnbounded<DtmfTone>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Channel<SessionSignal> _inboundSignals = Channel.CreateUnbounded<SessionSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private Task? _backgroundLoop;
    private int _disconnectFired;
    private readonly CallingTelemetry _telemetry;

    public AcsWebSocketEdge(
        WebSocket webSocket,
        CallConnectionProperties callConnection,
        CancellationToken httpContextCancellation,
        CallAutomationClient callAutomationClient,
        ILogger<AcsWebSocketEdge> logger,
        CallingTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);

        _webSocket = webSocket;
        _call = callConnection;
        _callAutomationClient = callAutomationClient;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(httpContextCancellation);
        _logger = logger;
        _telemetry = telemetry;

        Metadata = new CallEdgeMetadata
        {
            DisplayName = _call.SourceDisplayName ?? _call.Source.RawId,
            RawIdentifier = _call.Source.RawId,
            CorrelationId = _call.CorrelationId,
            ServerCallId = _call.ServerCallId
        };
    }



    public string EdgeId => _call.CallConnectionId;

    public CallEdgeKind Kind => CallEdgeKind.Caller;

    public CallEdgeMetadata Metadata { get; }

    public bool IsConnected => _backgroundLoop is { IsCompleted: false };

    public ChannelReader<AudioFrame> InboundAudio => _inboundAudio.Reader;

    public ChannelReader<DtmfTone> InboundDtmf => _inboundDtmf.Reader;

    public ChannelReader<SessionSignal> InboundSignals => _inboundSignals.Reader;

    public EdgeCapabilities Capabilities => EdgeCapabilities.Streaming | EdgeCapabilities.TransferCall;

    public bool CanControl => true;

    public event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;

    public async Task<ICallEdge> ConnectAsync(string callId, WebSocket webSocket, CancellationToken cancellationToken = default)
    {

        _telemetry.EdgeConnected(EdgeId, Kind);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            ReceiveLoopAsync(linked.Token),
            SendLoopAsync(linked.Token));
        return this;
    }
    public async Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken = default)
    {
        if (_callAutomationClient is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AcsWebSocketEdge)} {EdgeId} cannot hang up: no CallAutomationClient was provided.");
        }

        var connection = _callAutomationClient.GetCallConnection(_call.CallConnectionId);
        await connection.HangUpAsync(hangUpForEveryone, cancellationToken).ConfigureAwait(false);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_backgroundLoop is not null)
        {
            return Task.CompletedTask;
        }

        _telemetry.EdgeConnected(EdgeId, Kind);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _backgroundLoop = Task.WhenAll(
            ReceiveLoopAsync(linked.Token),
            SendLoopAsync(linked.Token));
        return Task.CompletedTask;
    }

    public async Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        if (_callAutomationClient is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AcsWebSocketEdge)} {EdgeId} cannot transfer: no CallAutomationClient was provided.");
        }

        var connection = _callAutomationClient.GetCallConnection(_call.CallConnectionId);
        var options = AcsCallControl.BuildTransferOptions(request);
        await connection.TransferCallToParticipantAsync(options, cancellationToken).ConfigureAwait(false);
    }



    public async ValueTask DispatchAsync(OutboundDirective directive, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (directive)
            {
                case OutboundDirective.Audio audio:
                    await _outbound.Writer.WriteAsync(audio.Frame.Pcm.ToArray(), cancellationToken).ConfigureAwait(false);
                    break;

                case OutboundDirective.StopPlayback:
                    // Drain any queued PCM frames first so the send loop doesn't flush
                    // stale agent audio after we signal stop (barge-in correctness).
                    while (_outbound.Reader.TryRead(out _))
                    {
                    }

                    if (_webSocket.State == WebSocketState.Open)
                    {
                        var stop = OutStreamingData.GetStopAudioForOutbound();
                        await _webSocket.SendAsync(
                            new ArraySegment<byte>(Encoding.UTF8.GetBytes(stop)),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case OutboundDirective.TransferCall transfer:
                    _logger.LogInformation(
                        "Edge {EdgeId} transferring call to {Target} ({Kind}); reason: {Reason}",
                        EdgeId, transfer.TargetIdentifier, transfer.Kind, transfer.Reason ?? "(none)");
                    var transferRequest = new TransferRequest(
                        transfer.TargetIdentifier,
                        transfer.Kind,
                        transfer.Reason is { Length: > 0 }
                            ? new Dictionary<string, string> { ["reason"] = transfer.Reason }
                            : null);
                    await TransferAsync(transferRequest, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    _logger.LogWarning(
                        "Streaming ACS edge {EdgeId} cannot dispatch {DirectiveKind}; pair this strategy with an AcsCallAutomationEdge instead",
                        EdgeId, directive.GetType().Name);
                    _telemetry.DirectiveUnsupported(EdgeId, directive.GetType().Name, Capabilities);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _telemetry.DirectiveDispatchFailed(EdgeId, directive.GetType().Name, ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Edge disposed",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow on shutdown */ }
        }

        _webSocket.Dispose();
        _cts.Dispose();

        await RaiseDisconnectedAsync(EdgeDisconnectedReason.SessionEnded).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    break;
                }

                var outbound = OutStreamingData.GetAudioDataForOutbound(bytes);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(outbound)),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "ACS edge {EdgeId} send loop terminated", EdgeId);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var bufferPool = ArrayPool<byte>.Shared;
        var reason = EdgeDisconnectedReason.NetworkError;

        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var buffer = bufferPool.Rent(64 * 1024);
                try
                {
                    var result = await _webSocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = EdgeDisconnectedReason.CallerHangup;
                        break;
                    }

                    var payload = buffer.AsMemory(0, result.Count).ToArray();
                    var parsed = TryParse(payload);
                    switch (parsed)
                    {
                        case AudioData audio when !audio.IsSilent:
                            await _inboundAudio.Writer.WriteAsync(
                                new AudioFrame(audio.Data, audio.Timestamp, EdgeId),
                                ct).ConfigureAwait(false);
                            break;

                        case DtmfData dtmf when !string.IsNullOrEmpty(dtmf.Data):
                            var digit = dtmf.Data[0];
                            await _inboundDtmf.Writer.WriteAsync(
                                new DtmfTone(digit, DateTimeOffset.UtcNow),
                                ct).ConfigureAwait(false);
                            break;
                    }
                }
                finally
                {
                    bufferPool.Return(buffer, clearArray: true);
                }
            }
        }
        catch (OperationCanceledException) { reason = EdgeDisconnectedReason.SessionEnded; }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "ACS edge {EdgeId} receive loop terminated", EdgeId);
            reason = EdgeDisconnectedReason.NetworkError;
        }
        finally
        {
            _inboundAudio.Writer.TryComplete();
            _inboundDtmf.Writer.TryComplete();
            _inboundSignals.Writer.TryComplete();
            await RaiseDisconnectedAsync(reason).ConfigureAwait(false);
        }
    }

    private async ValueTask RaiseDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0)
        {
            return;
        }

        _telemetry.EdgeDisconnected(EdgeId, Kind, reason);

        var handlers = Disconnected;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<EdgeDisconnectedReason, ValueTask>>())
        {
            try { await handler(reason).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Edge disconnected handler threw"); }
        }
    }

    private static StreamingData? TryParse(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : StreamingData.Parse(text);
    }
}
