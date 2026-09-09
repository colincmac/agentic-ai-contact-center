using System.Diagnostics;
using System.Threading.Channels;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Shared.Diagnostics;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Calling.Core;


/// <summary>
/// Default <see cref="ICallSession"/> implementation. Owns the call-scoped DI scope
/// and is responsible for:
/// <list type="bullet">
///   <item>connecting the caller edge,</item>
///   <item>starting the conversation strategy,</item>
///   <item>fanning caller audio to the strategy and (when attached) supervisor,</item>
///   <item>fanning strategy audio to the caller and (when attached) supervisor,</item>
///   <item>bridging supervisor audio to the caller during BargeIn,</item>
///   <item>fanning <c>strategy.Events</c> out to observers,</item>
///   <item>tearing everything down on caller hangup or end.</item>
/// </list>
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly IServiceScope _scope;
    private readonly ICallSessionRegistry _registry;
    private readonly ICallQualityReporter _quality;
    private readonly CallingTelemetry _telemetry;
    private readonly CallTierAdmission _tierAdmission;
    private readonly ILogger<CallSession> _logger;
    private readonly CallStateProjector _stateProjector;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lazy<Task> _initialization;
    private ICallQualityRegistration? _qualityRegistration;
    private Activity? _callActivity;
    private DateTimeOffset _lastStateChangeAt = DateTimeOffset.UtcNow;
    private long _firstAudioRecorded;
    private readonly List<ICallObserver> _observers;

    private readonly List<Channel<StrategyEvent>> _observerFanout = [];
    private readonly HashSet<int> _observerOverflowLogged = [];
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _callerEdgeGate = new(1, 1);
    private readonly SemaphoreSlim _supervisorGate = new(1, 1);

    // Caller inbound audio is teed at the session level so the supervisor can
    // listen in (Monitor) without affecting the strategy's view.
    private readonly Channel<AudioFrame> _strategyInbound = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<DtmfTone> _strategyDtmf = Channel.CreateUnbounded<DtmfTone>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private IConversationStrategy _strategy;

    // Caller-edge wiring. The active edge and its pumps are mutated under _stateLock.
    private ICallEdge? _callerEdge;
    private CancellationTokenSource? _callerEdgeCts;
    private Task? _callerEdgePumps;

    // Supervisor wiring. All four are mutated under _stateLock.
    private ICallEdge? _supervisorEdge;
    private SupervisorMode? _supervisorMode;
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorPumps;

    private Task? _eventPump;
    private Task? _persistenceMonitor;
    private Task? _endTask;
    private Task? _terminalReactionTask;
    private CallSessionState _state = CallSessionState.Created;
    private CallSessionState _stateBeforeSuspend = CallSessionState.Active;
    private int _strategyStarted;
    private int _disposed;

    public CallSession(
        IncomingCallContext call,
        CallSessionRouting routing,
        IConversationStrategy strategy,
        IServiceScope scope,

        ICallQualityReporter qualityReporter,
        ICallSessionRegistry registry,
        CallStateProjector sessionState,
        CallingTelemetry telemetry,
        CallTierAdmission tierAdmission,
        IEnumerable<ICallObserver>? observers = null,
        ILogger<CallSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        CallInformation = Throw.IfNull(call);
        Routing = Throw.IfNull(routing);

        _strategy = strategy;
        _observers = observers is not null ? new(observers) : [];
        _quality = qualityReporter;
        _scope = scope;
        _registry = registry;
        _telemetry = telemetry;
        _tierAdmission = tierAdmission;
        _logger = logger ?? NullLogger<CallSession>.Instance;

        _stateProjector = sessionState;

        StartedAt = DateTimeOffset.UtcNow;
        _lastStateChangeAt = StartedAt;
        _initialization = new(StartCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Identity + routing facts for this call.</summary>
    public IncomingCallContext CallInformation { get; }

    public string CallId => CallInformation.CallId;

    public CallSessionRouting Routing { get; }

    public CallSessionState State
    {
        get { lock (_stateLock) { return _state; } }
    }

    public DateTimeOffset StartedAt { get; }

    public ICallEdge? CallerEdge
    {
        get { lock (_stateLock) { return _callerEdge; } }
    }

    public IConversationStrategy Strategy => _strategy;

    public ICallEdge? SupervisorEdge
    {
        get { lock (_stateLock) { return _supervisorEdge; } }
    }

    public SupervisorMode? SupervisorMode
    {
        get { lock (_stateLock) { return _supervisorMode; } }
    }

    public IReadOnlyList<ICallObserver> Observers => _observers;

    public event Func<CallSessionState, ValueTask>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialization.IsValueCreated
            && State is CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted)
        {
            return Task.FromException(new InvalidOperationException(
                $"Call session '{CallId}' cannot initialize in state {State}."));
        }

        var initialization = _initialization.Value;
        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    private async Task StartCoreAsync()
    {
        var startedObservers = new List<ICallObserver>();
        try
        {
            _callActivity = _telemetry.StartCallActivity(CallId, _strategy.Tier, _strategy.Kind);
            _telemetry.CallStarted(CallId, _strategy.Tier);

            // Seed the dashboard so updates have something to mutate.
            var qualityRegistration = _quality.Register(new CallQualitySnapshot
            {
                CallId = CallId,
                State = CallSessionState.Created,
                ActiveTier = _strategy.Tier,
                StrategyKind = _strategy.Kind,
                StartedAt = StartedAt,
                UpdatedAt = StartedAt
            });
            _qualityRegistration = qualityRegistration;

            // Hydrate persisted call state (failover / mid-call resume) and start the background
            // persister BEFORE the event pump folds anything into it.
            await _stateProjector.HydrateAsync(_cts.Token).ConfigureAwait(false);
            _persistenceMonitor = ObservePersistenceAsync();

            // Wire observers BEFORE starting the event pump so no event is dropped.
            foreach (var observer in _observers)
            {
                var capacity = _scope.ServiceProvider.GetService<IOptions<CallStateOptions>>()?.Value.ObserverQueueCapacity ?? 256;
                ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
                var bridge = Channel.CreateBounded<StrategyEvent>(
                    new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
                _observerFanout.Add(bridge);

                await observer.StartAsync(new CallObservation
                {
                    CallId = CallId,
                    Events = bridge.Reader,
                    Quality = qualityRegistration,
                    Services = _scope.ServiceProvider
                }, _cts.Token).ConfigureAwait(false);
                startedObservers.Add(observer);
            }

            _eventPump = Task.Run(PumpEventsAsync, CancellationToken.None);
        }
        catch
        {
            if (State is not (CallSessionState.Ending or CallSessionState.Ended))
            {
                await TransitionAsync(CallSessionState.Faulted).ConfigureAwait(false);
            }
            foreach (var bridge in _observerFanout)
            {
                bridge.Writer.TryComplete();
            }
            foreach (var observer in startedObservers)
            {
                try { await observer.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Observer rollback failed during initialization"); }
            }
            Interlocked.Exchange(ref _qualityRegistration, null)?.Dispose();
            throw;
        }
    }

    private async Task ObservePersistenceAsync()
    {
        try
        {
            await _stateProjector.PersistenceCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                _logger.LogError(ex, "Call-state persistence failed for {CallId}; terminating the unsafe session.", CallId);
                RequestTermination("state_persistence_failed", faulted: true, ex);
            }
        }
    }

    public async Task<bool> AttachCallerEdgeAsync(
        ICallEdge callerEdge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerEdge);

        if (callerEdge.Kind is not CallEdgeKind.Caller)
        {
            throw new ArgumentException("Only caller edges can be attached as the active caller connection.", nameof(callerEdge));
        }

        await _callerEdgeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted
                || _callerEdge is not null)
            {
                return false;
            }

            var requiredCapabilities = _strategy.EmittedDirectives;
            var missingCapabilities = requiredCapabilities & ~callerEdge.Capabilities;
            if (requiredCapabilities is not EdgeCapabilities.None && missingCapabilities is not EdgeCapabilities.None)
            {
                _logger.LogWarning(
                    "Caller edge {EdgeId} cannot carry strategy {StrategyKind}; missing capabilities {MissingCapabilities}",
                    callerEdge.EdgeId,
                    _strategy.Kind,
                    missingCapabilities);
                return false;
            }

            await StartAsync(cancellationToken).ConfigureAwait(false);
            await TransitionAsync(CallSessionState.Connecting).ConfigureAwait(false);

            callerEdge.Disconnected += OnEdgeDisconnectedAsync;
            lock (_stateLock)
            {
                _callerEdge = callerEdge;
            }

            try
            {
                await callerEdge.ConnectAsync(cancellationToken).ConfigureAwait(false);
                StartCallerEdgePumps(callerEdge);

                if (Interlocked.CompareExchange(ref _strategyStarted, 1, 0) == 0)
                {
                    await _strategy.StartAsync(BuildStartContext(callerEdge), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _strategy.ResumeAsync(cancellationToken).ConfigureAwait(false);
                }

            }
            catch
            {
                await DetachCallerEdgeCoreAsync(suspendStrategy: false, CancellationToken.None).ConfigureAwait(false);
                await TransitionAsync(CallSessionState.Created).ConfigureAwait(false);
                throw;
            }

            await TransitionAsync(CallSessionState.Active).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _callerEdgeGate.Release();
        }
    }

    public async Task DetachCallerEdgeAsync(CancellationToken cancellationToken = default)
    {
        await _callerEdgeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DetachCallerEdgeCoreAsync(suspendStrategy: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _callerEdgeGate.Release();
        }
    }

    private async Task DetachCallerEdgeCoreAsync(
        bool suspendStrategy,
        CancellationToken cancellationToken)
    {
        ICallEdge? callerEdge;
        CancellationTokenSource? edgeCts;
        Task? edgePumps;
        lock (_stateLock)
        {
            callerEdge = _callerEdge;
            edgeCts = _callerEdgeCts;
            edgePumps = _callerEdgePumps;
            _callerEdge = null;
            _callerEdgeCts = null;
            _callerEdgePumps = null;
        }

        if (callerEdge is null)
        {
            return;
        }

        callerEdge.Disconnected -= OnEdgeDisconnectedAsync;

        if (edgeCts is not null)
        {
            try { await edgeCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
        }
        if (edgePumps is not null)
        {
            try { await edgePumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        edgeCts?.Dispose();

        if (suspendStrategy && Volatile.Read(ref _strategyStarted) != 0
            && State is not (CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted))
        {
            try { await _strategy.SuspendAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Strategy suspend on caller-edge detach failed"); }
            await TransitionAsync(CallSessionState.Suspended).ConfigureAwait(false);
        }

        try { await callerEdge.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Caller edge dispose failed for call {CallId}", CallId); }
    }

    public async Task<bool> AttachSupervisorAsync(
        ICallEdge supervisorEdge,
        SupervisorMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisorEdge);

        if (supervisorEdge.Kind is not CallEdgeKind.Supervisor)
        {
            throw new ArgumentException("Only supervisor edges can be attached as the active supervisor connection.", nameof(supervisorEdge));
        }

        await _supervisorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is CallSessionState.Ended or CallSessionState.Ending or CallSessionState.Faulted)
            {
                return false;
            }

            ICallEdge? existing;
            lock (_stateLock) { existing = _supervisorEdge; }
            if (existing is not null)
            {
                _logger.LogWarning("Supervisor already attached to call {CallId}; detach first", CallId);
                return false;
            }

            var published = false;
            var modeApplied = false;
            try
            {
                await supervisorEdge.ConnectAsync(cancellationToken).ConfigureAwait(false);
                supervisorEdge.Disconnected += OnSupervisorDisconnectedAsync;

                var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var pump = Task.Run(() => PumpSupervisorInboundAsync(supervisorEdge, pumpCts.Token), CancellationToken.None);

                lock (_stateLock)
                {
                    _supervisorEdge = supervisorEdge;
                    _supervisorMode = mode;
                    _supervisorCts = pumpCts;
                    _supervisorPumps = pump;
                }
                published = true;

                await ApplyModeAsync(mode, cancellationToken).ConfigureAwait(false);
                modeApplied = true;

                _qualityRegistration?.Update(current => current with
                {
                    Supervisor = new SupervisorPresence(
                        SupervisorId: supervisorEdge.EdgeId,
                        DisplayName: supervisorEdge.Metadata.DisplayName,
                        Mode: mode,
                        AttachedAt: DateTimeOffset.UtcNow)
                });
                _qualityRegistration?.RaiseAlert(new QualityAlert(
                    AlertId: $"supervisor-{supervisorEdge.EdgeId}",
                    Kind: QualityAlertKind.SupervisorWhisper,
                    Severity: QualityAlertSeverity.Info,
                    Message: $"Supervisor attached in {mode} mode",
                    RaisedAt: DateTimeOffset.UtcNow));

                _telemetry.SupervisorAttached(CallId, mode);
                using (var attachSpan = _telemetry.StartChildActivity(CallingActivitySource.SupervisorAttachActivityName, CallId))
                {
                    attachSpan?.SetTag(CallingActivitySource.SupervisorIdTag, supervisorEdge.EdgeId);
                    attachSpan?.SetTag(CallingActivitySource.SupervisorModeTag, mode.ToString());
                }
                _logger.LogInformation("Supervisor {SupervisorId} attached to call {CallId} in {Mode} mode",
                    supervisorEdge.EdgeId, CallId, mode);
                return true;
            }
            catch
            {
                if (published)
                {
                    try { await DetachSupervisorCoreAsync(CancellationToken.None, restoreStrategy: modeApplied).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Supervisor rollback failed for call {CallId}", CallId); }
                }
                else
                {
                    supervisorEdge.Disconnected -= OnSupervisorDisconnectedAsync;
                    try { await supervisorEdge.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Rejected supervisor edge dispose failed for call {CallId}", CallId); }
                }
                throw;
            }
        }
        finally
        {
            _supervisorGate.Release();
        }
    }

    public async Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default)
    {
        await _supervisorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SupervisorMode? current;
            lock (_stateLock)
            {
                if (_supervisorEdge is null)
                {
                    return false;
                }
                current = _supervisorMode;
            }

            if (current == mode)
            {
                return true;
            }

            await ApplyModeAsync(mode, cancellationToken).ConfigureAwait(false);
            try
            {
                _qualityRegistration?.Update(snapshot => snapshot.Supervisor is null
                    ? snapshot
                    : snapshot with { Supervisor = snapshot.Supervisor with { Mode = mode } });
            }
            catch
            {
                await ApplyModeAsync(current!.Value, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            lock (_stateLock) { _supervisorMode = mode; }
            _telemetry.SupervisorModeChanged(CallId, current, mode);
            _logger.LogInformation("Supervisor mode for call {CallId} changed: {From} → {To}", CallId, current, mode);
            return true;
        }
        finally
        {
            _supervisorGate.Release();
        }
    }

    public async Task DetachSupervisorAsync(CancellationToken cancellationToken = default)
    {
        await _supervisorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DetachSupervisorCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _supervisorGate.Release();
        }
    }

    private async Task DetachSupervisorCoreAsync(
        CancellationToken cancellationToken,
        bool restoreStrategy = true)
    {
        ICallEdge? supervisor;
        SupervisorMode? lastMode;
        CancellationTokenSource? pumpCts;
        Task? pumps;
        lock (_stateLock)
        {
            supervisor = _supervisorEdge;
            lastMode = _supervisorMode;
            pumpCts = _supervisorCts;
            pumps = _supervisorPumps;
            _supervisorEdge = null;
            _supervisorMode = null;
            _supervisorCts = null;
            _supervisorPumps = null;
        }

        if (supervisor is null)
        {
            return;
        }

        supervisor.Disconnected -= OnSupervisorDisconnectedAsync;

        if (pumpCts is not null)
        {
            try { await pumpCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
            pumpCts.Dispose();
        }
        if (pumps is not null)
        {
            try { await pumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        // If we were in BargeIn, lift the suspend now that the supervisor is gone.
        // Skip the resume / state revert when the call is already winding down — the
        // strategy is about to be stopped anyway and Ending → Active would be wrong.
        var endingNow = State is CallSessionState.Ending or CallSessionState.Ended;
        if (restoreStrategy && lastMode is Calling.SupervisorMode.BargeIn && !endingNow)
        {
            try { await _strategy.ResumeAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Strategy resume on detach failed"); }

            await TransitionAsync(_stateBeforeSuspend).ConfigureAwait(false);
        }

        try
        {
            _qualityRegistration?.Update(current => current with { Supervisor = null });
            _qualityRegistration?.ResolveAlert($"supervisor-{supervisor.EdgeId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supervisor quality cleanup failed for call {CallId}", CallId);
        }

        try { await supervisor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor edge dispose failed"); }

        _telemetry.SupervisorDetached(CallId, lastMode);
        _logger.LogInformation("Supervisor detached from call {CallId}", CallId);
    }

    public async Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newStrategy);

        using var span = _telemetry.StartChildActivity(CallingActivitySource.ReplaceStrategyActivityName, CallId);
        span?.SetTag("strategy.from.kind", _strategy.Kind.ToString());
        span?.SetTag("strategy.to.kind", newStrategy.Kind.ToString());

        await _callerEdgeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TransitionAsync(CallSessionState.Suspended).ConfigureAwait(false);

            ICallEdge? callerEdge;
            CancellationTokenSource? edgeCts;
            Task? edgePumps;
            lock (_stateLock)
            {
                callerEdge = _callerEdge;
                edgeCts = _callerEdgeCts;
                edgePumps = _callerEdgePumps;
                _callerEdgeCts = null;
                _callerEdgePumps = null;
            }
            if (edgeCts is not null)
            {
                await edgeCts.CancelAsync().ConfigureAwait(false);
            }
            if (edgePumps is not null)
            {
                try { await edgePumps.ConfigureAwait(false); } catch { /* strategy swap */ }
            }
            edgeCts?.Dispose();

            var old = Interlocked.Exchange(ref _strategy, newStrategy);
            try
            {
                if (Volatile.Read(ref _strategyStarted) != 0)
                {
                    await old.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                await old.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Old strategy disposal failed"); }

            if (callerEdge is not null)
            {
                await newStrategy.StartAsync(BuildStartContext(callerEdge), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _strategyStarted, 1);
                StartCallerEdgePumps(callerEdge);
                await TransitionAsync(CallSessionState.Active).ConfigureAwait(false);
            }
            else
            {
                Volatile.Write(ref _strategyStarted, 0);
            }
            return true;
        }
        finally
        {
            _callerEdgeGate.Release();
        }
    }

    public async Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var state = State;
        if (state is CallSessionState.Ended or CallSessionState.Ending or CallSessionState.Faulted)
        {
            _logger.LogWarning("Transfer requested for call {CallId} in terminal state {State}; ignoring", CallId, state);
            return;
        }

        var callerEdge = CallerEdge;
        if (callerEdge is not ICallControl control || !control.CanControl)
        {
            throw new InvalidOperationException(
                callerEdge is null
                    ? $"Call {CallId} cannot be transferred because no caller edge is attached."
                    : $"Call {CallId} cannot be transferred: caller edge {callerEdge.GetType().Name} does not support call control.");
        }

        using var span = _telemetry.StartChildActivity(CallingActivitySource.TransferActivityName, CallId);
        span?.SetTag(CallingActivitySource.TransferKindTag, request.Kind.ToString());
        span?.SetTag(CallingActivitySource.TransferTargetTag, request.TargetIdentifier);
        _telemetry.TransferInitiated(CallId, request.Kind);

        _logger.LogInformation(
            "Transferring call {CallId} to {Target} ({Kind})",
            CallId, request.TargetIdentifier, request.Kind);

        var stateBeforeTransfer = State;
        await TransitionAsync(CallSessionState.Transferring).ConfigureAwait(false);

        // Pause rather than stop so a rejected platform transfer can return the caller
        // to the same strategy and workflow state.
        try { await _strategy.SuspendAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(span, ex);
            await TransitionAsync(stateBeforeTransfer).ConfigureAwait(false);
            _logger.LogWarning(ex, "Strategy suspend on transfer failed for call {CallId}", CallId);
            throw;
        }

        try
        {
            await control.TransferAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(span, ex);
            try { await _strategy.ResumeAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception resumeEx)
            {
                _logger.LogWarning(resumeEx, "Strategy resume after transfer failure failed for call {CallId}", CallId);
                await TransitionAsync(CallSessionState.Faulted).ConfigureAwait(false);
                throw new AggregateException(ex, resumeEx);
            }
            await TransitionAsync(stateBeforeTransfer).ConfigureAwait(false);
            throw;
        }

        _qualityRegistration?.RaiseAlert(new QualityAlert(
            AlertId: $"transfer-{Guid.NewGuid():N}",
            Kind: QualityAlertKind.SupervisorWhisper,
            Severity: QualityAlertSeverity.Info,
            Message: $"Transfer initiated to {request.TargetIdentifier} ({request.Kind})",
            RaisedAt: DateTimeOffset.UtcNow));
    }

    public async Task HangUpAsync(bool hangUpForEveryone = true, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (State is CallSessionState.Ended or CallSessionState.Ending)
        {
            return;
        }

        using var span = _telemetry.StartChildActivity(CallingActivitySource.HangupActivityName, CallId);
        span?.SetTag(CallingActivitySource.HangupForEveryoneTag, hangUpForEveryone);
        span?.SetTag(CallingActivitySource.HangupReasonTag, reason);
        _telemetry.HangupIssued(CallId, hangUpForEveryone, reason);

        var callerEdge = CallerEdge;
        if (callerEdge is ICallControl control && control.CanControl)
        {
            try
            {
                _logger.LogInformation(
                    "Hanging up call {CallId} (forEveryone={ForEveryone}) reason={Reason}",
                    CallId, hangUpForEveryone, reason ?? "<none>");
                await control.HangUpAsync(hangUpForEveryone, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Platform hang-up failed for call {CallId}; tearing down locally", CallId);
            }
        }
        else
        {
            _logger.LogWarning(
                "Call {CallId} caller edge {EdgeKind} does not support call control; tearing down locally only",
                CallId, callerEdge?.GetType().Name ?? "<none>");
        }

        await EndAsync(reason ?? "hangup", cancellationToken).ConfigureAwait(false);
    }

    public Task EndAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        Task endTask;
        TaskCompletionSource? owner = null;
        lock (_stateLock)
        {
            if (_endTask is null)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _endTask = owner.Task;
            }
            endTask = _endTask;
        }

        if (owner is not null)
        {
            _ = CompleteEndAsync(owner, reason);
        }

        return cancellationToken.CanBeCanceled
            ? endTask.WaitAsync(cancellationToken)
            : endTask;
    }

    private async Task CompleteEndAsync(TaskCompletionSource completion, string? reason)
    {
        try
        {
            await EndCoreAsync(reason).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task EndCoreAsync(string? reason)
    {
        await TransitionAsync(CallSessionState.Ending).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_initialization.IsValueCreated)
        {
            try { await _initialization.Value.ConfigureAwait(false); }
            catch { /* initialization failure is followed by the same teardown */ }
        }

        await DetachSupervisorAsync(CancellationToken.None).ConfigureAwait(false);

        await _callerEdgeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DetachCallerEdgeCoreAsync(suspendStrategy: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _callerEdgeGate.Release();
        }

        if (Volatile.Read(ref _strategyStarted) != 0)
        {
            await _strategy.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _strategyInbound.Writer.TryComplete();
        _strategyDtmf.Writer.TryComplete();
        if (_eventPump is not null)
        {
            try { await _eventPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        // Persist final call state once the pump has drained every fold.
        if (_stateProjector is not null)
        {
            try { await _stateProjector.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Final call-state flush failed for call {CallId}", CallId); }
        }

        foreach (var observer in _observers)
        {
            try { await observer.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Observer stop failed"); }
        }

        await TransitionAsync(CallSessionState.Ended).ConfigureAwait(false);
        _registry.TryRemove(CallId, this);
        Interlocked.Exchange(ref _qualityRegistration, null)?.Dispose();

        var duration = DateTimeOffset.UtcNow - StartedAt;
        _telemetry.CallEnded(CallId, _strategy.Tier, State, reason, duration);
        if (_callActivity is not null)
        {
            _callActivity.SetTag(CallingActivitySource.CallEndReasonTag, reason ?? "unspecified");
            _callActivity.SetTag("call.duration_s", duration.TotalSeconds);
            _callActivity.Stop();
            _callActivity.Dispose();
            _callActivity = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { await EndAsync(reason: "disposed").ConfigureAwait(false); } catch { /* shutdown */ }
        Task? terminalReaction;
        lock (_stateLock) { terminalReaction = _terminalReactionTask; }
        if (terminalReaction is not null)
        {
            try { await terminalReaction.ConfigureAwait(false); } catch { /* observed during shutdown */ }
        }

        try { await _strategy.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }

        foreach (var observer in _observers)
        {
            try { await observer.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        Interlocked.Exchange(ref _qualityRegistration, null)?.Dispose();

        try { await _tierAdmission.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Tier admission release failed for call {CallId}", CallId); }

        if (_scope is IAsyncDisposable asyncScope)
        {
            try { await asyncScope.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }
        else
        {
            _scope.Dispose();
        }

        _cts.Dispose();
        _callerEdgeGate.Dispose();
        _supervisorGate.Dispose();
    }

    private void StartCallerEdgePumps(ICallEdge callerEdge)
    {
        var edgeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var pumps = Task.WhenAll(
            PumpCallerInboundAsync(callerEdge, edgeCts.Token),
            PumpCallerDtmfAsync(callerEdge, edgeCts.Token),
            PumpStrategyOutboundAsync(callerEdge, edgeCts.Token));

        lock (_stateLock)
        {
            _callerEdgeCts = edgeCts;
            _callerEdgePumps = pumps;
        }
    }

    private StrategyStartContext BuildStartContext(ICallEdge callerEdge) => new()
    {
        CallId = CallId,
        InboundAudio = _strategyInbound.Reader,
        InboundDtmf = _strategyDtmf.Reader,
        CallerMetadata = callerEdge.Metadata,
        EdgeCapabilities = callerEdge.Capabilities,
        InboundSignals = callerEdge.InboundSignals,
        Control = callerEdge as ICallControl,
        StateProjector = _stateProjector,
    };

    /// <summary>
    /// Reads caller audio off the edge and fans it to the strategy. When a
    /// supervisor is attached in Monitor or Whisper mode, the supervisor also
    /// hears the caller. In BargeIn mode the strategy stops receiving caller
    /// audio (the supervisor is talking instead).
    /// </summary>
    private async Task PumpCallerInboundAsync(ICallEdge callerEdge, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in callerEdge.InboundAudio.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _telemetry.InboundAudioFrame(callerEdge.EdgeId);
                ICallEdge? supervisor;
                SupervisorMode? mode;
                lock (_stateLock)
                {
                    supervisor = _supervisorEdge;
                    mode = _supervisorMode;
                }

                if (mode is not Calling.SupervisorMode.BargeIn)
                {
                    await _strategyInbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }

                if (supervisor is not null && mode is Calling.SupervisorMode.Monitor or Calling.SupervisorMode.Whisper
                    && supervisor.Capabilities.HasFlag(EdgeCapabilities.Audio))
                {
                    try { await supervisor.DispatchAsync(new OutboundDirective.Audio(frame), cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Supervisor caller-tap send failed"); }
                }
            }

            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                RequestTermination("caller_audio_completed", faulted: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                _logger.LogWarning(ex, "Caller inbound pump faulted for call {CallId}", CallId);
                RequestTermination("caller_audio_faulted", faulted: true, ex);
            }
        }
    }

    private async Task PumpCallerDtmfAsync(ICallEdge callerEdge, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var tone in callerEdge.InboundDtmf.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _strategyDtmf.Writer.WriteAsync(tone, cancellationToken).ConfigureAwait(false);
            }

            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                RequestTermination("caller_dtmf_completed", faulted: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* detach or shutdown */ }
        catch (Exception ex)
        {
            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                _logger.LogWarning(ex, "Caller DTMF pump faulted for call {CallId}", CallId);
                RequestTermination("caller_dtmf_faulted", faulted: true, ex);
            }
        }
    }

    /// <summary>
    /// Reads strategy outbound directives and dispatches them to the caller. In
    /// Monitor mode the supervisor receives a tap of any Audio directive (other
    /// directive kinds aren't audible — supervisors are streaming edges). In BargeIn
    /// the strategy is suspended and no directive reaches the caller.
    /// </summary>
    private async Task PumpStrategyOutboundAsync(ICallEdge callerEdge, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var directive in _strategy.Outbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ICallEdge? supervisor;
                SupervisorMode? mode;
                lock (_stateLock)
                {
                    supervisor = _supervisorEdge;
                    mode = _supervisorMode;
                }

                // BargeIn keeps strategy output off the caller's wire even if the
                // strategy hasn't drained yet from its suspend signal.
                if (mode is not Calling.SupervisorMode.BargeIn && callerEdge.IsConnected)
                {
                    if (callerEdge.Capabilities.HasFlag(DirectiveCapability(directive)))
                    {
                        var dispatchStart = Stopwatch.GetTimestamp();
                        try
                        {
                            await callerEdge.DispatchAsync(directive, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _telemetry.DirectiveDispatchFailed(callerEdge.EdgeId, directive.GetType().Name, ex);
                            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
                            {
                                _logger.LogWarning(ex, "Caller dispatch faulted for call {CallId}", CallId);
                                RequestTermination("caller_dispatch_faulted", faulted: true, ex);
                            }
                            return;
                        }
                        _telemetry.DirectiveDispatched(
                            callerEdge.EdgeId,
                            directive.GetType().Name,
                            Stopwatch.GetElapsedTime(dispatchStart));

                        if (directive is OutboundDirective.Audio
                            && Interlocked.Exchange(ref _firstAudioRecorded, 1) == 0)
                        {
                            _telemetry.RecordTimeToFirstAudio(CallId, _strategy.Tier, DateTimeOffset.UtcNow - StartedAt);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Caller edge for call {CallId} cannot dispatch {DirectiveKind}; capabilities are {Capabilities}",
                            CallId, directive.GetType().Name, callerEdge.Capabilities);
                        _telemetry.DirectiveUnsupported(callerEdge.EdgeId, directive.GetType().Name, callerEdge.Capabilities);
                        // Surface the mismatch so observers / dashboards can flag it.
                        var mismatch = new StrategyEvent.DispatchUnsupported(
                            directive.GetType().Name,
                            callerEdge.Capabilities,
                            DateTimeOffset.UtcNow);
                        PublishToObservers(mismatch);
                    }
                }

                if (supervisor is not null
                    && mode is Calling.SupervisorMode.Monitor
                    && directive is OutboundDirective.Audio agentAudio
                    && supervisor.Capabilities.HasFlag(EdgeCapabilities.Audio))
                {
                    try { await supervisor.DispatchAsync(agentAudio, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Supervisor agent-tap send failed"); }
                }
            }

            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                RequestTermination("strategy_outbound_completed", faulted: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            if (ShouldReactToCallerPumpExit(callerEdge, cancellationToken))
            {
                _logger.LogWarning(ex, "Strategy outbound reader faulted for call {CallId}", CallId);
                RequestTermination("strategy_outbound_faulted", faulted: true, ex);
            }
        }
    }

    private static EdgeCapabilities DirectiveCapability(OutboundDirective directive) => directive switch
    {
        OutboundDirective.Audio => EdgeCapabilities.Audio,
        OutboundDirective.SpeakText => EdgeCapabilities.SpeakText,
        OutboundDirective.PlayFile => EdgeCapabilities.PlayFile,
        OutboundDirective.StopPlayback => EdgeCapabilities.StopPlayback,
        OutboundDirective.CollectDtmf => EdgeCapabilities.CollectDtmf,
        _ => EdgeCapabilities.None
    };

    /// <summary>
    /// Reads supervisor inbound audio. In BargeIn it bridges to the caller. In
    /// Whisper it forwards to the strategy via <see cref="IWhisperableStrategy"/>
    /// when supported. In Monitor (the default tap-only mode) it is dropped.
    /// </summary>
    private async Task PumpSupervisorInboundAsync(ICallEdge supervisor, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in supervisor.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                SupervisorMode? mode;
                lock (_stateLock) { mode = _supervisorMode; }

                switch (mode)
                {
                    case Calling.SupervisorMode.BargeIn when CallerEdge is { IsConnected: true } callerEdge
                                                              && callerEdge.Capabilities.HasFlag(EdgeCapabilities.Audio):
                        try { await callerEdge.DispatchAsync(new OutboundDirective.Audio(frame), ct).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Supervisor BargeIn send failed"); }
                        break;

                    case Calling.SupervisorMode.Whisper when _strategy is IWhisperableStrategy whisperable:
                        try
                        {
                            await whisperable.InjectWhisperAsync(new SupervisorWhisper
                            {
                                SupervisorId = supervisor.EdgeId,
                                Audio = frame.Pcm,
                                At = frame.Timestamp
                            }, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "Whisper inject failed"); }
                        break;

                        // Monitor / Whisper-without-support: drop.
                }
            }
        }
        catch (OperationCanceledException) { /* swap or shutdown */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor inbound pump terminated"); }
    }

    /// <summary>
    /// Apply a mode transition: BargeIn suspends the strategy and saves the prior
    /// state for resume; any other mode resumes the strategy if we were suspended.
    /// </summary>
    private async Task ApplyModeAsync(SupervisorMode mode, CancellationToken ct)
    {
        if (mode is Calling.SupervisorMode.BargeIn)
        {
            lock (_stateLock)
            {
                if (_state is not CallSessionState.Suspended)
                {
                    _stateBeforeSuspend = _state;
                }
            }
            await _strategy.SuspendAsync(ct).ConfigureAwait(false);

            await TransitionAsync(CallSessionState.Suspended).ConfigureAwait(false);
        }
        else if (State is CallSessionState.Suspended)
        {
            await _strategy.ResumeAsync(ct).ConfigureAwait(false);

            CallSessionState stateBeforeSuspend;
            lock (_stateLock) { stateBeforeSuspend = _stateBeforeSuspend; }
            await TransitionAsync(stateBeforeSuspend).ConfigureAwait(false);
        }
    }

    private void PublishToObservers(StrategyEvent item)
    {
        for (var i = 0; i < _observerFanout.Count; i++)
        {
            if (_observerFanout[i].Writer.TryWrite(item)) { continue; }
            bool firstOverflow;
            lock (_stateLock) { firstOverflow = _observerOverflowLogged.Add(i); }
            if (firstOverflow)
            {
                _logger.LogWarning("Observer {ObserverId} queue overflowed for {CallId}; lossless={Lossless}.",
                    _observers[i].ObserverId, CallId, _observers[i].RequiresLosslessDelivery);
            }
            if (_observers[i].RequiresLosslessDelivery)
            {
                RequestTermination("required_observer_overflow", faulted: true);
            }
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var ev in _strategy.Events.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _telemetry.StrategyEventEmitted(CallId, ev);
                using (_telemetry.StartStrategyEventActivity(CallId, ev)) { /* span captured in using to record end time */ }

                // State folding happens at emit time via the strategy's FoldingChannelWriter funnel
                // (Option A), so this pump must NOT fold again — it only fans out to observers.
                PublishToObservers(ev);

                if (ev is StrategyEvent.Faulted fault)
                {
                    RequestTermination("strategy_faulted", faulted: true, fault.Exception);
                    return;
                }
            }

            if (!_cts.IsCancellationRequested
                && State is not (CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted))
            {
                RequestTermination("strategy_events_completed", faulted: true);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Strategy event reader faulted for call {CallId}", CallId);
            RequestTermination("strategy_events_faulted", faulted: true, ex);
        }
        finally
        {
            foreach (var bridge in _observerFanout)
            {
                bridge.Writer.TryComplete();
            }
        }
    }

    private ValueTask OnEdgeDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        _logger.LogInformation("Caller edge disconnected ({Reason}); ending call {CallId}", reason, CallId);
        RequestTermination(reason.ToString(), faulted: reason is EdgeDisconnectedReason.Faulted or EdgeDisconnectedReason.NetworkError);
        return ValueTask.CompletedTask;
    }

    private bool ShouldReactToCallerPumpExit(ICallEdge callerEdge, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        lock (_stateLock)
        {
            return ReferenceEquals(_callerEdge, callerEdge)
                && _state is not (CallSessionState.Ending or CallSessionState.Ended or CallSessionState.Faulted);
        }
    }

    private void RequestTermination(string reason, bool faulted, Exception? exception = null)
    {
        TaskCompletionSource? owner = null;
        lock (_stateLock)
        {
            if (_terminalReactionTask is null)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _terminalReactionTask = owner.Task;
            }
        }

        if (owner is not null)
        {
            _ = CompleteTerminationReactionAsync(owner, reason, faulted, exception);
        }
    }

    private async Task CompleteTerminationReactionAsync(
        TaskCompletionSource completion,
        string reason,
        bool faulted,
        Exception? exception)
    {
        try
        {
            if (faulted)
            {
                await TransitionAsync(CallSessionState.Faulted).ConfigureAwait(false);
            }
            await EndAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(exception is null ? ex : new AggregateException(exception, ex),
                "Terminal reaction failed for call {CallId}", CallId);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async ValueTask OnSupervisorDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        _logger.LogInformation("Supervisor edge disconnected ({Reason}) on call {CallId}", reason, CallId);
        try { await DetachSupervisorAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Detach-on-disconnect failed for call {CallId}", CallId); }
    }

    private async ValueTask TransitionAsync(CallSessionState target)
    {
        bool changed;
        CallSessionState previous;
        DateTimeOffset previousAt;
        lock (_stateLock)
        {
            previous = _state;
            previousAt = _lastStateChangeAt;
            changed = _state != target;
            _state = target;
            if (changed)
            {
                _lastStateChangeAt = DateTimeOffset.UtcNow;
            }
        }

        if (!changed)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - previousAt;
        _telemetry.StateTransition(CallId, previous, target, elapsed);
        _callActivity?.AddEvent(new ActivityEvent(
            name: "call.state_transition",
            tags: new ActivityTagsCollection
            {
                { CallingActivitySource.CallStateFromTag, previous.ToString() },
                { CallingActivitySource.CallStateToTag, target.ToString() },
                { "elapsed_ms", elapsed.TotalMilliseconds },
            }));
        if (target == CallSessionState.Faulted)
        {
            _telemetry.CallFaulted(CallId, _strategy.Tier, previous.ToString());
            _callActivity?.SetStatus(ActivityStatusCode.Error, "call faulted");
        }

        _qualityRegistration?.Update(current => current with { State = target });

        if (StateChanged is null)
        {
            return;
        }

        foreach (var handler in StateChanged.GetInvocationList().Cast<Func<CallSessionState, ValueTask>>())
        {
            try { await handler(target).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "StateChanged handler threw"); }
        }
    }
}
