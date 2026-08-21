//using System;
//using System.Collections.Generic;
//using System.Text;
//using Microsoft.Extensions.Logging;

//namespace Agents.AI.ContactCenter.PresenceDetection;


///// <summary>
///// Session-scoped presence detector that unifies all interaction modalities
///// (voice, chat, DTMF) into a single inactivity-timeout mechanism.
/////
///// Owns the state machine and timer directly. Subscribes to the session's
///// <see cref="HubSessionEventBus"/> for chat/transcript events and exposes
///// methods for transports to inject VAD and DTMF signals.
/////
///// When the inactivity timeout fires, a <see cref="HubSessionEventKind.PresenceTimeout"/>
///// event is published to the bus so any participant can react.
/////
///// Simplified Flow:
///// - Person activity (any modality) → stop timer, transition to PersonSpeaking / PersonActive
///// - Person stops speaking → start timer with short buffer
///// - Agent response started → pause timer
///// - Agent response completed → resume timer with buffer
///// - Prompt started → pause timer
///// - Prompt completed → resume timer with buffer
///// - Timeout reached → publish PresenceTimeout event
///// </summary>
//public sealed class PresenceDetectorService : IAsyncDisposable
//{
//    private readonly PresenceDetectionTimer _timer;
//    private readonly string _sessionId;
//    private readonly ILogger<PresenceDetectorService> _logger;
//    private readonly Lock _stateLock = new();
//    private readonly Dictionary<InputModality, ModalityActivity> _lastActivity = [];

//    private PresenceState _state = PresenceState.Idle;
//    private bool _isPromptPlaying;
//    private int _disposed;

//    /// <summary>
//    /// Default inactivity timeout in milliseconds (30 seconds).
//    /// </summary>
//    public const int DefaultTimeoutMs = 30_000;

//    /// <summary>
//    /// Optional tool call monitor. When set, the detector pauses monitoring
//    /// while tool calls are active.
//    /// </summary>
//    public IToolCallMonitor? ToolCallMonitor { get; set; }

//    /// <summary>
//    /// Raised when presence state changes, enabling external components to react.
//    /// </summary>
//    public event Action<PresenceState, InputModality?>? StateChanged;

//    public PresenceDetectorService(
//        HubSessionEventBus eventBus,
//        string sessionId,
//        int timeoutMs = DefaultTimeoutMs,
//        IToolCallMonitor? toolCallMonitor = null,
//        ILoggerFactory? loggerFactory = null)
//    {
//        _eventBus = eventBus;
//        _sessionId = sessionId;
//        _logger = loggerFactory?.CreateLogger<PresenceDetectorService>()
//                  ?? NullLogger<PresenceDetectorService>.Instance;

//        _timer = new PresenceDetectionTimer(
//            timeoutMs,
//            OnTimeoutAsync,
//            loggerFactory?.CreateLogger<PresenceDetectionTimer>());

//        ToolCallMonitor = toolCallMonitor;
//    }

//    /// <summary>
//    /// Gets the current state of the presence detection state machine.
//    /// </summary>
//    public PresenceState CurrentState
//    {
//        get
//        {
//            lock (_stateLock)
//            {
//                return _state;
//            }
//        }
//    }

//    /// <summary>
//    /// Gets a snapshot of the last activity detected per input modality.
//    /// </summary>
//    public IReadOnlyDictionary<InputModality, ModalityActivity> LastActivityByModality
//    {
//        get
//        {
//            lock (_stateLock)
//            {
//                return new Dictionary<InputModality, ModalityActivity>(_lastActivity);
//            }
//        }
//    }

//    #region Lifecycle

//    /// <summary>
//    /// Starts listening to the event bus for presence-relevant events.
//    /// Call this after the session is fully initialized.
//    /// </summary>
//    public void StartListening()
//    {
//        if (_subscription is not null)
//        {
//            return;
//        }

//        _subscription = _eventBus.Subscribe(static evt =>
//            evt.Kind is HubSessionEventKind.ChatMessage or HubSessionEventKind.Transcript);

//        _ = ProcessEventsAsync();

//        _logger.LogInformation("PresenceDetectorService started for session {SessionId}.", _sessionId);
//    }

//    /// <summary>
//    /// Stops all monitoring and resets to idle.
//    /// </summary>
//    public void Reset()
//    {
//        lock (_stateLock)
//        {
//            _timer.StopMonitoring();
//            _isPromptPlaying = false;
//            _lastActivity.Clear();
//            TransitionTo(PresenceState.Idle, null);
//        }
//    }

//    #endregion

//    #region Transport signals — call these from transports

//    /// <summary>
//    /// Injects a VAD event from the Realtime AI transport.
//    /// </summary>
//    public void OnVadEvent(VadEventType vadEvent)
//    {
//        switch (vadEvent)
//        {
//            case VadEventType.InputSpeechStarted:
//                OnPersonActivityDetected(InputModality.Voice);
//                break;

//            case VadEventType.InputSpeechEnded:
//                OnPersonStoppedSpeaking();
//                break;

//            case VadEventType.OutputSpeechStarted:
//                OnAgentResponseStarted();
//                break;

//            case VadEventType.OutputSpeechEnded:
//                OnAgentResponseCompleted();
//                break;
//        }
//    }

//    /// <summary>
//    /// Injects a DTMF key press signal.
//    /// </summary>
//    public void OnDtmfReceived(string dtmfKey)
//    {
//        OnPersonActivityDetected(InputModality.Dtmf, dtmfKey);
//    }

//    /// <summary>
//    /// Called when an inbound chat message is received.
//    /// </summary>
//    public void OnChatMessageReceived(string? messagePreview = null)
//    {
//        if (string.IsNullOrWhiteSpace(messagePreview))
//        {
//            return;
//        }

//        OnPersonActivityDetected(InputModality.Chat, messagePreview);

//        lock (_stateLock)
//        {
//            if (!ShouldPauseMonitoring())
//            {
//                _timer.StartMonitoring(500); // Longer buffer — person may be typing
//                TransitionTo(PresenceState.Active, InputModality.Chat);
//            }
//        }
//    }

//    /// <summary>
//    /// Called when a prompt starts playing.
//    /// </summary>
//    public void OnPromptStarted(Guid requestId)
//    {
//        lock (_stateLock)
//        {
//            _isPromptPlaying = true;
//            _timer.StopMonitoring();
//            TransitionTo(PresenceState.Paused, null);
//        }
//    }

//    /// <summary>
//    /// Called when a prompt finishes playing.
//    /// </summary>
//    public void OnPromptCompleted(int bufferMs, bool isPresenceCheck = false)
//    {
//        lock (_stateLock)
//        {
//            _isPromptPlaying = false;

//            if (isPresenceCheck)
//            {
//                TransitionTo(PresenceState.Idle, null);
//                return;
//            }

//            StartMonitoringIfAllowed(bufferMs, null);
//        }
//    }

//    #endregion

//    #region State-machine internals

//    private void OnPersonActivityDetected(InputModality modality, string? detail = null)
//    {
//        lock (_stateLock)
//        {
//            _lastActivity[modality] = new ModalityActivity(modality, DateTimeOffset.UtcNow, detail);

//            var newState = modality switch
//            {
//                InputModality.Voice => PresenceState.PersonSpeaking,
//                _ => PresenceState.PersonActive
//            };

//            _timer.StopMonitoring();
//            TransitionTo(newState, modality);
//        }
//    }

//    private void OnPersonStoppedSpeaking()
//    {
//        lock (_stateLock)
//        {
//            if (ShouldPauseMonitoring())
//            {
//                TransitionTo(PresenceState.Paused, InputModality.Voice);
//                return;
//            }

//            _timer.StartMonitoring(100); // Small buffer after speech ends
//            TransitionTo(PresenceState.Active, InputModality.Voice);
//        }
//    }

//    private void OnAgentResponseStarted()
//    {
//        lock (_stateLock)
//        {
//            _timer.StopMonitoring();
//            TransitionTo(PresenceState.Paused, null);
//        }
//    }

//    private void OnAgentResponseCompleted(int bufferMs = 500)
//    {
//        lock (_stateLock)
//        {
//            StartMonitoringIfAllowed(bufferMs, null);
//        }
//    }

//    private void StartMonitoringIfAllowed(int bufferMs, InputModality? modality)
//    {
//        if (ShouldPauseMonitoring())
//        {
//            return;
//        }

//        _timer.StartMonitoring(bufferMs);
//        TransitionTo(PresenceState.Active, modality);
//    }

//    private bool ShouldPauseMonitoring() =>
//        _isPromptPlaying || (ToolCallMonitor?.IsActive() ?? false);

//    private void TransitionTo(PresenceState newState, InputModality? modality)
//    {
//        if (_state == newState)
//        {
//            return;
//        }

//        _state = newState;
//        StateChanged?.Invoke(_state, modality);
//    }

//    #endregion

//    #region Event bus integration

//    private async Task ProcessEventsAsync()
//    {
//        if (_subscription is null)
//        {
//            return;
//        }

//        try
//        {
//            await foreach (var evt in _subscription.ReadAllAsync())
//            {
//                try
//                {
//                    ProcessEvent(evt);
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "Error processing event {EventId} for presence detection.", evt.EventId);
//                }
//            }
//        }
//        catch (OperationCanceledException)
//        {
//            // Expected during shutdown
//        }
//    }

//    private void ProcessEvent(SessionContextEvent evt)
//    {
//        switch (evt.Kind)
//        {
//            case HubSessionEventKind.ChatMessage:
//                string? preview = null;
//                if (evt.Payload is MessageUpdate mu
//                    && mu.Contents.OfType<TextContent>().FirstOrDefault() is { } tc)
//                {
//                    preview = tc.Text;
//                }
//                OnChatMessageReceived(preview);
//                break;

//            case HubSessionEventKind.Transcript:
//                // Treat transcript arrival as voice activity if no VAD signal handled it already
//                OnPersonActivityDetected(InputModality.Voice);
//                break;
//        }
//    }

//    private async Task OnTimeoutAsync()
//    {
//        _logger.LogWarning("Presence timeout for session {SessionId}.", _sessionId);

//        await _eventBus.PublishAsync(new SessionContextEvent
//        {
//            EventId = $"presence_timeout_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
//            Kind = HubSessionEventKind.PresenceTimeout,
//            SourceParticipantId = _sessionId,
//            Payload = new PresenceTimeoutPayload
//            {
//                SessionId = _sessionId,
//                TimeoutAt = DateTimeOffset.UtcNow,
//                LastActivityByModality = LastActivityByModality
//            }
//        }).ConfigureAwait(false);
//    }

//    #endregion

//    public async ValueTask DisposeAsync()
//    {
//        if (Interlocked.Exchange(ref _disposed, 1) != 0)
//        {
//            return;
//        }

//        if (_subscription is not null)
//        {
//            await _subscription.DisposeAsync();
//        }

//        _timer.Dispose();
//    }
//}

///// <summary>
///// Payload published to the event bus when a presence timeout occurs.
///// </summary>
//public sealed record PresenceTimeoutPayload
//{
//    public required string SessionId { get; init; }
//    public required DateTimeOffset TimeoutAt { get; init; }
//    public IReadOnlyDictionary<InputModality, ModalityActivity>? LastActivityByModality { get; init; }
//}
