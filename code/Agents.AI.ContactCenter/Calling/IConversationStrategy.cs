using System.Threading.Channels;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// The "brain" of an IVR call: takes caller input, produces audio + workflow events.
/// </summary>
public interface IConversationStrategy : IAsyncDisposable
{
    StrategyKind Kind { get; }

    /// <summary>Tier this strategy implements, for capacity tracking and dashboards.</summary>
    AgentTier Tier { get; }

    /// <summary>
    /// The folded IVR workflow slice for this call (current step, completed steps, collected slots, status).
    /// Backed by the call's <c>CallStateProjector</c>, which is shared across tier swaps and survives pod
    /// failover — so it replaces the old shared mutable state object as the degradation hand-off.
    /// </summary>
    IvrSnapshot WorkflowState { get; }

    /// <summary>The set of <see cref="OutboundDirective"/> kinds this strategy emits.</summary>
    EdgeCapabilities EmittedDirectives { get; }

    /// <summary>
    /// Outbound directives the session pumps to the caller edge: audio frames
    /// (streaming edges), or speak/play/recognize verbs (verb-based edges).
    /// </summary>
    ChannelReader<OutboundDirective> Outbound { get; }

    /// <summary>
    /// Structured events emitted by the strategy: transcripts, agent insights,
    /// workflow transitions, intent classifications, function call requests, etc.
    /// Consumed by the session for context projection and by observers.
    /// </summary>
    ChannelReader<StrategyEvent> Events { get; }

    Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default);


    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause output (e.g., supervisor barge-in). Inbound is still delivered so the
    /// strategy stays caught up; the session simply stops pumping OutboundAudio.
    /// </summary>
    ValueTask SuspendAsync(CancellationToken cancellationToken = default);

    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything a strategy needs from the call container at start.
/// </summary>
public sealed record StrategyStartContext
{
    public required string CallId { get; init; }

    /// <summary>Caller audio fanned in from the call edge.</summary>
    public required ChannelReader<AudioFrame> InboundAudio { get; init; }

    public required ChannelReader<DtmfTone> InboundDtmf { get; init; }

    /// <summary>
    /// Snapshot of the caller-edge metadata (phone number, display name, server call id).
    /// Strategies pass this into authenticators and may include it in observability events.
    /// Null only when the strategy is started without a caller edge (e.g. self-test harnesses).
    /// </summary>
    public CallEdgeMetadata? CallerMetadata { get; init; }

    /// <summary>
    /// The call's state projector, when the <c>AddCallState</c> plane is configured. Strategies wrap
    /// their downstream event writer in a <see cref="StateFoldingChannelWriter"/> over this projector so every
    /// emitted <see cref="StrategyEvent"/> folds synchronously at emit time (read-after-write). Null when
    /// no state plane is registered, in which case the funnel simply forwards events unfolded.
    /// </summary>
    public CallStateProjector? StateProjector { get; init; }


}

public enum StrategyKind
{
    RealtimeVoice,
    SttTts,
    Nlu,
    Dtmf,
    AgentEnsemble,
    Composite
}
