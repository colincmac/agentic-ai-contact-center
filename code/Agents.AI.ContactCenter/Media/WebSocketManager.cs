using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Media;


/// <summary>
/// Manages ACS WebSocket connections with support for supersession.
/// When a newer WebSocket arrives for the same correlation ID, the older
/// connection is gracefully closed before the replacement is activated.
/// </summary>
public sealed class WebSocketResourceManager : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, WebSocketConnectionState> _activeConnections = new();

    public WebSocketResourceManager(ILogger<WebSocketResourceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the current count of tracked connections.</summary>
    public int ConnectionCount => _activeConnections.Count;

    /// <summary>
    /// Registers a WebSocket for <paramref name="correlationId"/>. If an older connection
    /// exists (by <paramref name="connectionTime"/>), the old one is superseded and the
    /// <paramref name="onSuperseded"/> callback fires. If the incoming connection is older
    /// than the current one, registration is rejected.
    /// </summary>
    /// <returns>
    /// A <see cref="WebSocketRegistrationResult"/> indicating whether the connection was
    /// accepted, superseded an existing one, or was rejected.
    /// </returns>
    public async Task<WebSocketRegistrationResult> RegisterAsync(
        string correlationId,
        DateTime connectionTime,
        Func<Task<WebSocket>> acceptWebSocketAsync,
        Func<WebSocket, Task>? onSuperseded = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        ArgumentNullException.ThrowIfNull(acceptWebSocketAsync);

        var state = _activeConnections.GetOrAdd(correlationId, _ => new WebSocketConnectionState());

        await state.Semaphore.WaitAsync();
        try
        {
            // Reject if the incoming connection is older than the current one
            if (state.CurrentWebSocket is not null && connectionTime < state.ConnectionTime)
            {
                _logger.LogInformation(
                    "Rejected older WebSocket for {CorrelationId}. Incoming: {Incoming}, Current: {Current}",
                    correlationId, connectionTime, state.ConnectionTime);

                return WebSocketRegistrationResult.Rejected;
            }

            var previousWebSocket = state.CurrentWebSocket;
            var wasSuperseded = previousWebSocket is not null;

            // Accept the new connection
            var webSocket = await acceptWebSocketAsync();

            // Supersede the old connection
            if (wasSuperseded && previousWebSocket is not null)
            {
                _logger.LogInformation(
                    "Superseding WebSocket for {CorrelationId}. Old: {OldTime}, New: {NewTime}",
                    correlationId, state.ConnectionTime, connectionTime);

                if (onSuperseded is not null)
                {
                    try
                    {
                        await onSuperseded(previousWebSocket);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error in superseded callback for {CorrelationId}", correlationId);
                    }
                }
            }

            state.CurrentWebSocket = webSocket;
            state.ConnectionTime = connectionTime;
            state.OnSuperseded = onSuperseded;

            _logger.LogInformation(
                "Registered WebSocket for {CorrelationId}. Superseded: {Superseded}",
                correlationId, wasSuperseded);

            return wasSuperseded
                ? WebSocketRegistrationResult.Superseded(previousWebSocket!)
                : WebSocketRegistrationResult.Accepted(webSocket);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering WebSocket for {CorrelationId}", correlationId);
            return WebSocketRegistrationResult.Rejected;
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    /// <summary>
    /// Unregisters the WebSocket for <paramref name="correlationId"/> only if
    /// the provided <paramref name="webSocket"/> is the currently active connection.
    /// </summary>
    public async Task<bool> UnregisterAsync(string correlationId, WebSocket webSocket)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            _logger.LogWarning("Cannot unregister WebSocket: correlationId is null or empty");
            return false;
        }

        if (!_activeConnections.TryGetValue(correlationId, out var state))
        {
            return false;
        }

        await state.Semaphore.WaitAsync();
        try
        {
            if (!ReferenceEquals(state.CurrentWebSocket, webSocket))
            {
                return false;
            }

            _activeConnections.TryRemove(correlationId, out _);
            state.CurrentWebSocket = null;

            _logger.LogInformation("Unregistered WebSocket for {CorrelationId}", correlationId);
            return true;
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _activeConnections)
        {
            var state = kvp.Value;
            if (state.CurrentWebSocket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await state.CurrentWebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Manager disposing",
                        CancellationToken.None);
                }
                catch
                {
                    // Best-effort close
                }
            }
            state.Semaphore.Dispose();
        }
        _activeConnections.Clear();
    }

    private sealed class WebSocketConnectionState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public WebSocket? CurrentWebSocket { get; set; }
        public DateTime ConnectionTime { get; set; } = DateTime.MinValue;
        public Func<WebSocket, Task>? OnSuperseded { get; set; }
    }
}

/// <summary>
/// Result of a WebSocket registration attempt.
/// </summary>
public sealed class WebSocketRegistrationResult
{
    public bool IsAccepted { get; private init; }
    public bool WasSuperseded { get; private init; }
    public WebSocket? WebSocket { get; private init; }
    public WebSocket? SupersededWebSocket { get; private init; }

    public static WebSocketRegistrationResult Rejected => new() { IsAccepted = false };

    public static WebSocketRegistrationResult Accepted(WebSocket webSocket) => new()
    {
        IsAccepted = true,
        WasSuperseded = false,
        WebSocket = webSocket
    };

    public static WebSocketRegistrationResult Superseded(WebSocket supersededWebSocket) => new()
    {
        IsAccepted = true,
        WasSuperseded = true,
        SupersededWebSocket = supersededWebSocket
    };
}
