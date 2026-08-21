using Microsoft.Agents.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow;

public sealed class RealtimeConversationTurn
{
    public DateTimeOffset? Timestamp { get; set; }
    public string? UserMessageText { get; set; }
    public string? AgentResponseText { get; set; }
    public AgentResponse? AgentRun { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = new();
}
