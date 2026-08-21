using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.ToolApproval;


[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequiresUserApprovalAttribute(string? approvalRequestMessage = null) : Attribute, IToolApprovalRequirementData
{
    public string? ApprovalRequestMessage { get; set; } = approvalRequestMessage;

    public IEnumerable<IToolApprovalRequirement> GetRequirements() => [
        new RequiresUserApprovalRequirement(ApprovalRequestMessage)
    ];
}


public class RequiresUserApprovalRequirement : ToolApprovalHandler<RequiresUserApprovalRequirement>, IToolApprovalRequirement
{
    public string? ApprovalRequestMessage { get; }

    public AIContent? OnFailureResponse => new TextContent(ApprovalRequestMessage ?? "This tool requires user approval before it can be executed.");

    public RequiresUserApprovalRequirement(string? approvalRequestMessage = null)
    {
        ApprovalRequestMessage = approvalRequestMessage;
    }

    protected override Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresUserApprovalRequirement requirement)
    {

        return Task.CompletedTask;
    }
}



