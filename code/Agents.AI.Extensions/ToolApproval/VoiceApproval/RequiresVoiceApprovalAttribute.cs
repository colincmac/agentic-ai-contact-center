namespace Agents.AI.Extensions.ToolApproval.VoiceApproval;

// Attribute to mark tools as requiring voice approval
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresVoiceApprovalAttribute : Attribute, IToolApprovalRequirementData
{
    public string? ApprovalPrompt { get; }

    public RequiresVoiceApprovalAttribute(string? approvalMessage = null)
    {
        ApprovalPrompt = approvalMessage;
    }
    

    public IEnumerable<IToolApprovalRequirement> GetRequirements()
    {
        yield return new RequiresVoiceApprovalRequirement(ApprovalPrompt);
    }
}
