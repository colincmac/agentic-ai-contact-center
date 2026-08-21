using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public interface IRealtimeAgent
{
    ValueTask<RealtimeAIAgentSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default);

    Task SendAsync(
        RealtimeAIAgentSession session,
        RealtimeClientMessage message,
        CancellationToken cancellationToken = default);
}
