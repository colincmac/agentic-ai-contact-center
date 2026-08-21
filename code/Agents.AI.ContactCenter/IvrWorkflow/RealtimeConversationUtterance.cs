using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

public sealed class RealtimeConversationUtterance(ChatMessage message)
{
    public DateTimeOffset UtteranceStartTime { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UtteranceEndTime { get; set; }

    public ChatMessage Message { get; set; } = message;

    public TimeSpan? TurnDuration => UtteranceEndTime.HasValue ? UtteranceEndTime - UtteranceStartTime : null;
}
