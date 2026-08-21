using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.ToolApproval;


public record ToolApprovalState(
        string AgentContextId,
        string ThreadId,
        DateTimeOffset RequestedAt,
        DateTimeOffset? ExpiresAt = null,
        string? RequestedOnBehalfOfId = null,
        string? Description = null,
        ToolApprovalStatus Status = ToolApprovalStatus.PendingApproval)
{
    public string? Id { get; set; } = null;
    public string? ToolName { get; set; } = null;
    public string? FunctionCallId { get; set; } = null;

    public DateTime? DecisionTimestamp { get; set; }
    public string? DecisionUserId { get; set; }
    public string? DecisionResponse { get; set; } 

    [JsonIgnore]
    public bool IsApproved => Status == ToolApprovalStatus.Approved;
};

public enum ToolApprovalStatus
{
    Approved,
    PendingApproval,
    PendingAuthorization,
    Cancelled,
    Expired,
    Rejected
}

public sealed class ApprovalRequest
{
    public required string AgentContextId { get; set; }
    public required string ThreadId { get; set; }
    public required AIFunctionArguments? Arguments { get; set; }
    public required AIFunction Tool { get; set; }
    public required string FunctionCallId { get; set; }
    public string? Description { get; set; }
    public ChatMessage? AIUpdate { get; set; }
    public string? RequestedOnBehalfOfId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public List<IToolApprovalRequirement> Requirements { get; set; } = [];

    //public ToolApprovalState AsApprovalRecord() => new (ApprovalId, AgentContextId, RequestedAt, ExpiresAt, RequestedOnBehalfOfId, Description) { ToolName = ToolName, FunctionCallId = FunctionCallId };
}

public sealed record ApprovalResult(
    string ApprovalId,
    ToolApprovalStatus Status,
    string DecisionUserId,
    DateTimeOffset? DecisionTimestamp = null,
    string? DecisionResponse = null,
    string? ErrorMessage = null)
{
    public string ApprovalId { get; set; } = ApprovalId;
    public bool IsApproved => Status == ToolApprovalStatus.Approved;
    public ToolApprovalResponseContent ToResponseContent(FunctionCallContent originalFunctionCall) => new ToolApprovalResponseContent(ApprovalId, IsApproved, originalFunctionCall);
}
public sealed class PendingApproval
{
    //public ToolApprovalState ApprovalState { get; set; }

    //public required ApprovalRequest Request { get; set; }
    //public required TaskCompletionSource<ApprovalResult> CompletionSource { get; set; }
}
