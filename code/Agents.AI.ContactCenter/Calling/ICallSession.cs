namespace Agents.AI.ContactCenter.Calling;

/// <summary>One active IVR call.</summary>
public interface ICallSession : IAsyncDisposable
{
    IncomingCallContext CallInformation { get; }

    string CallId { get; }

    CallSessionState State { get; }

    DateTimeOffset StartedAt { get; }

    /// <summary>The currently attached caller edge, or <see langword="null"/> while awaiting a connection.</summary>
    ICallEdge? CallerEdge { get; }

    IConversationStrategy Strategy { get; }

    ICallEdge? SupervisorEdge { get; }

    SupervisorMode? SupervisorMode { get; }

    IReadOnlyList<ICallObserver> Observers { get; }

    /// <summary>Initialize call-scoped state and observers without requiring an active connection.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attach the call's active caller edge and start interaction. A session accepts at most one
    /// caller edge at a time; callers must detach the current edge before attaching a replacement.
    /// </summary>
    /// <returns><see langword="false"/> when an edge is already attached or the session is terminal.</returns>
    Task<bool> AttachCallerEdgeAsync(ICallEdge callerEdge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detach and dispose the active caller edge while preserving workflow and projected call state.
    /// This operation is idempotent.
    /// </summary>
    Task DetachCallerEdgeAsync(CancellationToken cancellationToken = default);

    /// <summary>Attach a supervisor edge for monitor / whisper / barge-in.</summary>
    Task<bool> AttachSupervisorAsync(
        ICallEdge supervisorEdge,
        SupervisorMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Change supervisor's mode without re-attaching (Monitor → BargeIn → Monitor).</summary>
    Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default);

    Task DetachSupervisorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Swap the strategy mid-call. Used by the composite fallback orchestrator and
    /// by tests. Workflow state is preserved automatically.
    /// </summary>
    Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default);

    /// <summary>Initiate transfer (TPE → Dynamics CCaaS). Strategy stops, edge survives until ACS confirms.</summary>
    Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hang up the call leg through the caller edge's platform call-control surface
    /// (ACS Call Automation today). This is the AI-callable / supervisor-callable
    /// equivalent of the caller hanging up. Implementations should also tear down
    /// local session resources (equivalent to <see cref="EndAsync(string?, CancellationToken)"/>).
    /// </summary>
    /// <param name="hangUpForEveryone">When <see langword="true"/>, ends the call for all parties.</param>
    /// <param name="reason">Optional human-readable reason recorded in telemetry.</param>
    Task HangUpAsync(bool hangUpForEveryone = true, string? reason = null, CancellationToken cancellationToken = default);

    Task EndAsync(string? reason = null, CancellationToken cancellationToken = default);

    event Func<CallSessionState, ValueTask>? StateChanged;
}

public enum CallSessionState
{
    Created,
    Connecting,
    Active,
    Suspended,           // strategy paused (e.g., supervisor BargeIn)
    Transferring,
    Ending,
    Ended,
    Faulted
}

public enum SupervisorMode
{
    /// <summary>Listen-only. Strategy continues. Supervisor hears caller + agent audio.</summary>
    Monitor,

    /// <summary>Whisper to the AI ensemble (or human agent later) without the caller hearing.
    /// For AI calls, supervisor speech is fed to the ensemble as an out-of-band system message.</summary>
    Whisper,

    /// <summary>Take over speaker role. Strategy is suspended; supervisor audio bridges to caller.</summary>
    BargeIn
}

/// <summary>
/// Factory used by the call automation handler when an IncomingCall event arrives.
/// Replaces today's <c>IContactCenterConversationSessionActivator</c> + hub indirection.
/// Resolves the strategy synchronously so no caller audio can arrive before the brain is wired.
/// </summary>
public interface ICallSessionFactory
{
    Task<CallSessionAcquisition> CreateAsync(CallSessionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Result of acquiring the one initialized local session for a call.</summary>
/// <param name="Session">The initialized session.</param>
/// <param name="Created">
/// True for the one local caller that owns the post-creation side effect, such as answering the call.
/// </param>
public sealed record CallSessionAcquisition(ICallSession Session, bool Created);

/// <summary>
/// Live registry of active call sessions. Replaces the dictionary on
/// ContactCenterConversationHub plus its IHostedService surface.
/// </summary>
public interface ICallSessionRegistry
{
    ICallSession? TryGet(string callId);
    bool TryAdd(ICallSession session);

    IReadOnlyCollection<ICallSession> ActiveSessions { get; }

    bool TryRemove(string callId, ICallSession expectedSession);
}

public sealed record TransferRequest(
    string TargetIdentifier,                            // E.164 / Teams ID / ACS user
    TransferKind Kind,
    IReadOnlyDictionary<string, string>? CustomContext);

public enum TransferKind
{
    BlindToPhoneNumber,
    BlindToTeamsUser,
    Consultative
}
