using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.ContactCenter.Media.Messaging;

/// <summary>
/// A transport that receives inbound messages and forwards them into the session via a registered callback.
/// </summary>
public interface IMessageProducer
{
    /// <summary>
    /// Register an inbound message handler. The router calls this to hook messages pushed from the transport.
    /// </summary>
    void SetOnMessageReceivedCallback(Func<string, MessageUpdate, CancellationToken, Task> handler);
}
