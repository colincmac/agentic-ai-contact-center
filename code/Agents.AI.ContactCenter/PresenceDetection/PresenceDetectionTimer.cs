using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.PresenceDetection;




/// <summary>
/// Represents the input modality through which a person is interacting.
/// </summary>
public enum InputModality
{
    /// <summary>Real-time voice input confirmed by the AI model (e.g., OpenAI VAD).</summary>
    Voice,

    /// <summary>Text chat message from a peer chat AI agent or messaging channel.</summary>
    Chat,

    /// <summary>DTMF tone input from a phone keypad.</summary>
    Dtmf,

    /// <summary>Tool or function call activity (person triggered an action).</summary>
    ToolCall
}

/// <summary>
/// Represents the possible states of presence detection in a realtime session.
/// </summary>
public enum PresenceState
{
    /// <summary>Not monitoring for presence (initial state or stopped).</summary>
    Idle,

    /// <summary>Actively monitoring for presence.</summary>
    Active,

    /// <summary>Temporarily paused (agent speaking, tool executing, or prompt playing).</summary>
    Paused,

    /// <summary>Person is currently speaking (voice detected by AI model).</summary>
    PersonSpeaking,

    /// <summary>Person is active through other input (e.g., DTMF, chat) but not speaking.</summary>
    PersonActive
}

/// <summary>
/// Tracks when the last activity occurred for a specific modality.
/// </summary>
public sealed record ModalityActivity(InputModality Modality, DateTimeOffset Timestamp, string? Detail = null);

/// <summary>
/// Optional interface for monitoring active tool calls. When provided, the presence detector
/// will pause presence monitoring while tool calls are in-flight.
/// </summary>
public interface IToolCallMonitor
{
    /// <summary>Returns true if one or more tool calls are currently executing.</summary>
    bool IsActive();
}

/// <summary>
/// Low-level timer for presence detection. Fires a callback when
/// no person activity is detected within the configured timeout.
///
/// Thread-Safety: All public methods are thread-safe and can be called concurrently.
/// </summary>
public sealed class PresenceDetectionTimer : IDisposable
{
    private readonly Func<Task> _onTimeout;
    private readonly int _timeoutMs;
    private readonly Timer _timer;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    private int _timerVersion;
    private int _expectedTimerVersion;
    private bool _disposed;
    private bool _isRunning;

    /// <summary>
    /// Creates a new PresenceDetectionTimer.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds before firing the callback.</param>
    /// <param name="onTimeout">Callback invoked when the timeout is reached.</param>
    /// <param name="logger">Optional logger.</param>
    public PresenceDetectionTimer(
        int timeoutMs,
        Func<Task> onTimeout,
        ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentNullException.ThrowIfNull(onTimeout);

        _timeoutMs = timeoutMs;
        _onTimeout = onTimeout;
        _logger = logger ?? NullLogger.Instance;
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Whether the timer is currently running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>
    /// Starts monitoring. If already running, resets the timer.
    /// </summary>
    /// <param name="additionalDelayMs">Optional additional delay added to the base timeout.</param>
    public void StartMonitoring(int additionalDelayMs = 0)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _timerVersion++;
                _expectedTimerVersion = _timerVersion;

                var totalTimeoutMs = _timeoutMs + additionalDelayMs;
                _timer.Change(totalTimeoutMs, Timeout.Infinite);
                _isRunning = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Timer already disposed, cannot start monitoring.");
            }
        }
    }

    /// <summary>
    /// Resets the timer to the base timeout. Called when any person activity is detected.
    /// </summary>
    public void Ping()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _timerVersion++;
            _expectedTimerVersion = _timerVersion;

            try
            {
                _timer.Change(_timeoutMs, Timeout.Infinite);
                _isRunning = true;
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Stops the timer without executing the callback.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (_disposed || !_isRunning)
            {
                return;
            }

            try
            {
                _timerVersion++;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _isRunning = false;
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnTimerCallback(object? state)
    {
        bool shouldFire;
        lock (_lock)
        {
            if (_disposed || _timerVersion != _expectedTimerVersion || !_isRunning)
            {
                return;
            }

            _isRunning = false;
            shouldFire = true;
        }

        if (shouldFire)
        {
            _ = ExecuteCallbackAsync();
        }
    }

    private async Task ExecuteCallbackAsync()
    {
        try
        {
            await _onTimeout().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in presence detection callback.");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isRunning = false;
        }

        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        _timer.Dispose();
    }
}
