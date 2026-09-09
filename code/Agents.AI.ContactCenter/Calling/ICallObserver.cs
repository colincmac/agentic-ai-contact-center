using System.Threading.Channels;

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — side-cars that observe a call without participating in its media path.
//
// Replaces today's "observer transports" (ConversationAnalysisTransport,
// A2AAgentTransport-as-listener) which had to fake being IChannelTransport just
// to subscribe to events. Observers don't pretend to be wires.
//
// The session starts each registered observer at call-start with a CallObservation
// view. Observers call back through ICallQualityReporter to update the dashboard.

/// <summary>
/// A passive participant in a call: receives the unified event stream and optional
/// audio taps, produces quality signals or external side effects.
/// </summary>
public interface ICallObserver : IAsyncDisposable
{
    string ObserverId { get; }
    /// <summary>When true, queue overflow terminates the call instead of dropping optional analytics.</summary>
    bool RequiresLosslessDelivery => false;

    Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What the session exposes to observers. Read-only — observers never push back
/// into the call's media path; they push quality updates through
/// <see cref="ICallQualityReporter"/> and alerts through there as well.
/// </summary>
public sealed record CallObservation
{
    public required string CallId { get; init; }
    public required ChannelReader<StrategyEvent> Events { get; init; }
    public required ICallQualityRegistration Quality { get; init; }

    /// <summary>Caller-side audio tap. Null if the observer didn't request audio access.</summary>
    public IAudioTap? CallerAudio { get; init; }

    /// <summary>Outbound (what the caller heard) tap. Null if not requested.</summary>
    public IAudioTap? AgentAudio { get; init; }

    public IServiceProvider Services { get; init; } = null!;
}

/// <summary>
/// A non-consuming subscription to a stream of audio frames. Multiple observers
/// can tap the same edge without affecting playback or each other.
/// </summary>
public interface IAudioTap : IAsyncDisposable
{
    ChannelReader<AudioFrame> Frames { get; }
}

/// <summary>
/// Concrete observer kinds we'll provide out of the box. Implementations live
/// outside this contract file.
///
/// SentimentObserver           — runs ITextSentimentAnalyzer over Transcript events,
///                               updates Sentiment + EscalationRisk.
/// AcousticEmotionObserver     — taps caller audio, runs IAudioAnalysisPipeline,
///                               updates SignalAgreement.
/// PresenceObserver            — wraps PresenceDetectorService, raises LongSilence alerts.
/// CallRecordingObserver       — taps both audio sides, writes to blob.
/// DashboardProjectionObserver — turns StrategyEvents into LiveCallRegistry updates.
/// AgentEnsembleObserver       — surfaces AgentSpeakingChanged + DelegateInsight events
///                               into the snapshot's ActiveSpeakerAgentId / DelegateTasks.
/// </summary>
internal static class ObserverKinds_Documentation
{
    // intentionally empty — XML doc above is the contract
}
