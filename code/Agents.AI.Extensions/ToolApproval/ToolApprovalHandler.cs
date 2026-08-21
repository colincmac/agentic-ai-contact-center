using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.ToolApproval;

public interface IToolApprovalRequirement
{
    AIContent? OnFailureResponse { get; }
}
public interface IToolApprovalRequirementData
{
    IEnumerable<IToolApprovalRequirement> GetRequirements();

}
public interface IToolApprovalHandler
{
    Task HandleAsync(ToolApprovalContext context);
}

public abstract class ToolApprovalHandler<TRequirement> : IToolApprovalHandler
        where TRequirement : IToolApprovalRequirement
{
    /// <summary>
    /// Makes a decision if authorization is allowed.
    /// </summary>
    /// <param name="context">The authorization context.</param>
    public virtual async Task HandleAsync(ToolApprovalContext context)
    {
        foreach (var req in context.PendingRequirements.OfType<TRequirement>())
        {
            await HandleRequirementAsync(context, req).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes a decision if authorization is allowed based on a specific requirement.
    /// </summary>
    /// <param name="context">The authorization context.</param>
    /// <param name="requirement">The requirement to evaluate.</param>
    protected abstract Task HandleRequirementAsync(ToolApprovalContext context, TRequirement requirement);
}
