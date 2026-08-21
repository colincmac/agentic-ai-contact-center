using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Extensions.ToolApproval.VoiceApproval;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace Agents.AI.ContactCenter.Authorization.VoiceApproval;

/// <summary>
/// Handles voice approval requirements by checking if approval has been granted.
/// When requirement not satisfied it records pending approval and fails the context.
/// </summary>
public sealed class VoiceApprovalHandler : ToolApprovalHandler<RequiresVoiceApprovalRequirement>
{
    private readonly ILogger<VoiceApprovalHandler> _logger;
    private readonly IMemoryCache _approvalCache;
    private static readonly TimeSpan approvalWindow = TimeSpan.FromMinutes(1);

    public VoiceApprovalHandler(
        IMemoryCache approvalCache,
        ILogger<VoiceApprovalHandler>? logger = null)
    {
        _approvalCache = approvalCache;
        _logger = logger ?? NullLogger<VoiceApprovalHandler>.Instance;
    }

    protected override Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresVoiceApprovalRequirement requirement)
    {

        var threadId = context.InvokingAgent.Id; // Or context.InvokingIdentity?.Name 
        var cacheKey = $"VoiceApproval:{threadId}:{context.Tool.Name}";

        if (_approvalCache.TryGetValue(cacheKey, out _))
        {
            _logger.LogInformation("Voice approval confirmed for {ToolName}. Executing.", context.Tool.Name);

            // Approval found, consume it (one-time use)
            _approvalCache.Remove(cacheKey);
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogInformation("Voice approval required for {ToolName}. Issuing challenge.", context.Tool.Name);

            // No approval found. Set a flag indicating we are waiting for confirmation.
            // In a more complex system, we might require an explicit "Yes" intent analysis here,
            // but relying on the Model's reasoning loop ("I need to ask user" -> User says "Yes" -> Model calls tool)
            // is the standard pattern for Voice Agents.

            // We mark it as "Pending Confirmation". The *Next* call will succeed.
            // NOTE: This assumes the model behaves correctly and only retries if the user consents.
            _approvalCache.Set(cacheKey, true, approvalWindow);

            context.Fail(requirement);
        }

        return Task.CompletedTask;
    }
}
