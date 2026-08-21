using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.ToolApproval.VoiceApproval;

/// <summary>
/// Requires voice confirmation from the participant before executing a tool
/// </summary>
public sealed class RequiresVoiceApprovalRequirement : IToolApprovalRequirement
{
    public RequiresVoiceApprovalRequirement(string? approvalPrompt = null)
    {
        ApprovalPrompt = approvalPrompt;
    }

    public string? ApprovalPrompt { get; }

    public AIContent? OnFailureResponse => new TextContent(
        ApprovalPrompt ?? "This action requires approval from the user. Please say 'yes' to approve or 'no' to cancel.");

}
