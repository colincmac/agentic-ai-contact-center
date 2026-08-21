using System.Security.Claims;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Extensions.ToolApproval;


/// <summary>
/// Provides contextual information and state management for a tool approval workflow, including requirements,
/// arguments, agent identity, and approval status.
/// </summary>
/// <remarks>
/// Use this class to track the progress and outcome of a tool invocation that requires approval. It
/// maintains the set of pending approval requirements, records failure reasons, and exposes properties to determine
/// whether the approval process has succeeded or failed. The context also provides access to the invoking agent, user
/// identity, and invocation arguments. Thread safety is not guaranteed; concurrent access should be managed externally
/// if required.</remarks>
public class ToolApprovalContext
{
    private readonly HashSet<IToolApprovalRequirement> _pendingRequirements;
    private List<AIContent>? _failedReasons;
    private readonly AIFunctionArguments _arguments;
    private bool _failCalled = false;
    private bool _succeedCalled = false;

    public ToolApprovalContext(
        AIFunction tool,
        AIFunctionArguments arguments,
        AIAgent invokingAgent,
        List<IToolApprovalRequirement>? requirements = null,
        ClaimsPrincipal? invokingIdentity = null
    )
    {
        Tool = Throw.IfNull(tool);
        _arguments = Throw.IfNull(arguments);
        InvokingAgent = invokingAgent;
        InvokingIdentity = invokingIdentity;
        _pendingRequirements = requirements is null ? [] : new (requirements);
    }

    /// <summary>
    /// The collection of all the <see cref="IToolApprovalRequirement"/> for the current tool invocation.
    /// </summary>
    public virtual IReadOnlyCollection<IToolApprovalRequirement> PendingRequirements => _pendingRequirements.AsReadOnly();
    public virtual IReadOnlyCollection<AIContent> FailureResponses
        => (IReadOnlyCollection<AIContent>?) _failedReasons ?? [];


    /// <summary>
    /// The <see cref="ClaimsPrincipal"/> representing the current user.
    /// </summary>
    public virtual ClaimsPrincipal? InvokingIdentity { get; }


    public virtual AIFunction Tool { get; }
    public virtual AIAgent InvokingAgent { get; }
    public virtual AIFunctionArguments Arguments => new (_arguments);


    public virtual bool HasFailed { get { return _failCalled; } }

    public virtual bool HasSucceeded
    {
        get
        {
            return !_failCalled && _succeedCalled && PendingRequirements.Count == 0;
        }
    }

    private void Fail()
    {
        _failCalled = true;
    }

    public virtual void Fail(IToolApprovalRequirement requirement)
    {
        Fail();
        if(requirement.OnFailureResponse is { } response)
        {
            _failedReasons ??= [];
            _failedReasons.Add(response);
        }
    }

    public virtual void Succeed(IToolApprovalRequirement requirement)
    {
        _succeedCalled = true;
        _pendingRequirements.Remove(requirement);
    }
}
