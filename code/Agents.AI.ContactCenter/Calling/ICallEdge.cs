using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Signaling;

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — proposed replacement for the "real wire" half of IChannelTransport.
// Owns a socket and nothing else. No workflow, no AI, no DI scope.
//
// Today's wire-owning transports (AcsWebsocketTransport, SignalRTransport,
// GrpcTransport) collapse into ICallEdge implementations. The "AI brain"
// transports (RealtimeVoiceAgentTransport, SttTtsAgentTransport, NluIntentTransport,
// DtmfIvrTransport, ChatAIAgentTransport) become IConversationStrategy instead.

/// <summary>
/// One end of an active call's media path (PSTN-backed ACS WebSocket today,
/// browser SignalR audio for a supervisor tomorrow, SIP for direct routing later).
/// </summary>
/// <remarks>
/// A <see cref="ICallSession"/> has at most one active <see cref="CallEdgeKind.Caller"/>
/// edge at a time. The caller edge may be detached and replaced while the session preserves
/// workflow state. A session may also attach an optional <see cref="CallEdgeKind.Supervisor"/>
/// edge for monitor / whisper / barge-in.
/// </remarks>
public interface ICallEdge : IAsyncDisposable
{
    string EdgeId { get; }

    CallEdgeKind Kind { get; }

    CallEdgeMetadata Metadata { get; }

    bool IsConnected { get; }

    /// <summary>Inbound caller audio frames. Single reader (the session).</summary>
    ChannelReader<AudioFrame> InboundAudio { get; }

    /// <summary>Inbound DTMF tones (RFC 2833 normalized by ACS). Single reader.</summary>
    ChannelReader<DtmfTone> InboundDtmf { get; }

    /// <summary>Inbound non-media signals (VAD, hold, hangup intent, etc.).</summary>
    ChannelReader<SessionSignal> InboundSignals { get; }

    /// <summary>The set of <see cref="OutboundDirective"/> kinds this edge can dispatch.</summary>
    EdgeCapabilities Capabilities { get; }

    /// <summary>
    /// Hand a directive to the edge. Edges drop (with a logged warning) any
    /// directive whose kind is not present in <see cref="Capabilities"/>.
    /// </summary>
    ValueTask DispatchAsync(OutboundDirective directive, CancellationToken cancellationToken = default);

    /// <summary>Open the wire and begin populating the inbound channels.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closed gracefully or due to error; fires exactly once.</summary>
    event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;
}

/// <summary>
/// Bit flags describing which <see cref="OutboundDirective"/> kinds an edge handles.
/// Used by the session at start-up to validate strategy/edge pairings, and by the
/// composite to choose strategies the edge can actually carry.
/// </summary>
[Flags]
public enum EdgeCapabilities
{
    None = 0,
    Audio = 1 << 0,
    SpeakText = 1 << 1,
    PlayFile = 1 << 2,
    StopPlayback = 1 << 3,
    CollectDtmf = 1 << 4,
    TransferCall = 1 << 5,
    AddParticipant = 1 << 6,

    /// <summary>Streaming edges: PCM audio in/out + barge-in stop.</summary>
    Streaming = Audio | StopPlayback,

    /// <summary>Verb-based edges: ACS Call Automation REST verbs.</summary>
    Verb = SpeakText | PlayFile | StopPlayback | CollectDtmf,
}

public enum CallEdgeKind
{
    Caller,
    Supervisor,
    Bridged,        // remote leg after a transfer that we still observe
    Synthetic       // for test harnesses
}

public sealed record CallEdgeMetadata
{
    public required string DisplayName { get; init; }
    public required string RawIdentifier { get; init; }
    public string? CorrelationId { get; init; }
    public string? ServerCallId { get; init; }
    public AudioFormat InboundFormat { get; init; } = AudioFormat.Pcm16Khz16BitMono;
    public AudioFormat OutboundFormat { get; init; } = AudioFormat.Pcm16Khz16BitMono;
}

public enum AudioFormat
{
    Pcm16Khz16BitMono,
    Pcm24Khz16BitMono,
    Pcm8Khz16BitMono
}

/// <summary>
/// A single chunk of PCM audio with the timestamp the wire produced/consumed it.
/// Replaces the bare <c>ReadOnlyMemory&lt;byte&gt;</c> currently passed around so
/// observers can correlate audio across edges and against transcripts.
/// </summary>
public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Pcm,
    DateTimeOffset Timestamp,
    string? SourceEdgeId = null);

public readonly record struct DtmfTone(char Digit, DateTimeOffset Timestamp);

public enum EdgeDisconnectedReason
{
    CallerHangup,
    NetworkError,
    ServerClose,
    SessionEnded,
    Timeout,
    Faulted
}
