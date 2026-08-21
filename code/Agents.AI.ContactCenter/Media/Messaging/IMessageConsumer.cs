using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.ContactCenter.Media.Messaging;

/// <summary>
/// A transport that can deliver messages outbound (e.g., to a WebSocket, SignalR client, or AI agent).
/// </summary>
public interface IMessageConsumer
{
    /// <summary>Send a message envelope to the transport.</summary>
    Task SendMessageAsync(MessageUpdate message, CancellationToken cancellationToken = default);
}
