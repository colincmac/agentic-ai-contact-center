using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Signaling;

namespace Agents.AI.ContactCenter.Tests.Helpers;

/// <summary>
/// In-memory <see cref="ICallEdge"/> for tests that need to exercise the call session
/// pipeline without an ACS or WebSocket dependency.
/// </summary>
internal sealed class FakeCallerEdge : ICallEdge, ICallControl
{
    private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateUnbounded<AudioFrame>();
    private readonly Channel<DtmfTone> _inboundDtmf = Channel.CreateUnbounded<DtmfTone>();
    private readonly Channel<SessionSignal> _inboundSignals = Channel.CreateUnbounded<SessionSignal>();
    private readonly Channel<OutboundDirective> _sentDirectives = Channel.CreateUnbounded<OutboundDirective>();
    private readonly Channel<AudioFrame> _sentAudio = Channel.CreateUnbounded<AudioFrame>();
    private bool _connected;
    private int _disconnectFired;
    private readonly Func<CancellationToken, Task>? _connectAsync;
    private readonly Exception? _transferException;
    private readonly Exception? _dispatchException;

    public FakeCallerEdge(string edgeId, CallEdgeKind kind = CallEdgeKind.Caller, string? displayName = null,
        EdgeCapabilities capabilities = EdgeCapabilities.Streaming,
        bool canControl = false,
        Func<CancellationToken, Task>? connectAsync = null,
        Exception? transferException = null,
        Exception? dispatchException = null)
    {
        EdgeId = edgeId;
        Kind = kind;
        Capabilities = capabilities;
        CanControl = canControl;
        _connectAsync = connectAsync;
        _transferException = transferException;
        _dispatchException = dispatchException;
        Metadata = new CallEdgeMetadata
        {
            DisplayName = displayName ?? $"fake-{kind.ToString().ToLowerInvariant()}",
            RawIdentifier = edgeId
        };
    }

    public string EdgeId { get; }
    public CallEdgeKind Kind { get; }
    public CallEdgeMetadata Metadata { get; }
    public bool IsConnected => _connected;
    public EdgeCapabilities Capabilities { get; }
    public bool CanControl { get; }
    public int DisposeCount { get; private set; }
    public int TransferCount { get; private set; }

    public ChannelReader<AudioFrame> InboundAudio => _inboundAudio.Reader;
    public ChannelReader<DtmfTone> InboundDtmf => _inboundDtmf.Reader;
    public ChannelReader<SessionSignal> InboundSignals => _inboundSignals.Reader;

    /// <summary>Every directive the session asked us to dispatch, in order.</summary>
    public Channel<OutboundDirective> SentDirectives => _sentDirectives;

    /// <summary>Convenience view: only the audio frames the session dispatched (Audio directives).</summary>
    public Channel<AudioFrame> Sent => _sentAudio;

    public event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connectAsync is not null)
        {
            await _connectAsync(cancellationToken);
        }
        _connected = true;
    }

    public Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken = default)
        => CanControl
            ? Task.CompletedTask
            : Task.FromException(new InvalidOperationException("Call control is disabled."));

    public Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        TransferCount++;
        return _transferException is null
            ? Task.CompletedTask
            : Task.FromException(_transferException);
    }

    public async ValueTask DispatchAsync(OutboundDirective directive, CancellationToken cancellationToken = default)
    {
        if (_dispatchException is not null)
        {
            throw _dispatchException;
        }
        await _sentDirectives.Writer.WriteAsync(directive, cancellationToken).ConfigureAwait(false);
        if (directive is OutboundDirective.Audio audio)
        {
            await _sentAudio.Writer.WriteAsync(audio.Frame, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask PushDtmfAsync(char digit)
        => _inboundDtmf.Writer.WriteAsync(new DtmfTone(digit, DateTimeOffset.UtcNow));

    public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm)
        => _inboundAudio.Writer.WriteAsync(new AudioFrame(pcm, DateTimeOffset.UtcNow, EdgeId));

    public async Task HangupAsync()
    {
        _connected = false;
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
        _inboundSignals.Writer.TryComplete();

        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0)
        {
            return;
        }

        var handlers = Disconnected;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<EdgeDisconnectedReason, ValueTask>>())
        {
            await handler(EdgeDisconnectedReason.CallerHangup);
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _connected = false;
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
        _inboundSignals.Writer.TryComplete();
        _sentDirectives.Writer.TryComplete();
        _sentAudio.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
