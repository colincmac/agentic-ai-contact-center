using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
namespace Agents.AI.Extensions.SessionManagement;




public interface IAgentSessionRegistry
{
    /// <summary>
    /// Registers a callback for a specific session to receive external messages.
    /// </summary>
    Task RegisterSession(string sessionId, Func<ChatMessage, CancellationToken, Task> messageHandler, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters the session.
    /// </summary>
    Task UnregisterSession(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the active session identified by sessionId.
    /// </summary>
    Task NotifySessionAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);
}

public class AgentSessionRegistry : IAgentSessionRegistry
{
    private readonly ConcurrentDictionary<string, Func<ChatMessage, CancellationToken, Task>> _activeSessions = new();

    public Task RegisterSession(string sessionId, Func<ChatMessage, CancellationToken, Task> messageHandler, CancellationToken cancellationToken = default)
    {
        _activeSessions[sessionId] = messageHandler;
        return Task.CompletedTask;
    }

    public Task UnregisterSession(string sessionId, CancellationToken cancellationToken = default)
    {
        _activeSessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public async Task NotifySessionAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (_activeSessions.TryGetValue(sessionId, out var handler))
        {
            await handler(message, cancellationToken);
        }
        // TODO: If session not found, you might want to log it or store it as a "missed" notification
    }
}
