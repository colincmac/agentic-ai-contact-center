using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.ToolApproval;

public record ToolApprovalFailure(AIFunction tool, AIFunctionArguments Arguments, List<IToolApprovalRequirement>? failedRequirements = null, List<AIContent>? failureResponses = null, bool? explicitlyFailed = null)
{
    public List<IToolApprovalRequirement> FailedRequirements { get; init; } = failedRequirements ?? [];
    public AIFunction Tool { get; } = tool;
    public AIFunctionArguments Arguments { get; } = Arguments;
    public List<AIContent> FailureResponses { get; init; } = failureResponses ?? [];
    public bool? ExplicitlyFailed { get; init; } = explicitlyFailed ?? false;

    public ChatMessage FailureResponseMessage => new (ChatRole.Tool, [.. FailureResponses]);
};

public class ToolApprovalResult
{

    private ToolApprovalResult() { }

    /// <summary>
    /// True if authorization was successful.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded { get; private set; }

    public ToolApprovalFailure? Failure { get; private set; }

    public object? FunctionCallResponse { get; private set; }

    /// <summary>
    /// Returns a successful result.
    /// </summary>
    /// <returns>A successful result.</returns>
    public static ToolApprovalResult Success(object? functionResponse) => new() { Succeeded = true, FunctionCallResponse = functionResponse };

    /// <summary>
    /// Creates a failed authorization result.
    /// </summary>
    /// <param name="failure">Contains information about why authorization failed.</param>
    /// <returns>The <see cref="ToolApprovalResult"/>.</returns>
    public static ToolApprovalResult Failed(ToolApprovalFailure failure) => new() { Failure = failure };
}
