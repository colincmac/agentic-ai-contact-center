using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Agents.AI.Extensions.ToolApproval;

public interface IToolApprovalStore
{
    IAsyncEnumerable<ToolApprovalState> GetApprovalsAsync(string agentContextId, CancellationToken cancellationToken = default);
    Task<ToolApprovalState> GetApprovalAsync(string agentContextId, string approvalId, CancellationToken cancellationToken = default);
    Task UpsertApprovalAsync(string agentContextId, string approvalId, ToolApprovalState approval, CancellationToken cancellationToken = default);
}

public class InMemoryToolApprovalStore : IToolApprovalStore
{
    private readonly ConcurrentDictionary<string, List<ToolApprovalState>> _approvalMap = new();
    public async IAsyncEnumerable<ToolApprovalState> GetApprovalsAsync(string agentContextId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_approvalMap.TryGetValue(agentContextId, out var approvals))
        {
            foreach (var approval in approvals)
            {
                yield return approval;
            }
        }
    }
    public Task<ToolApprovalState> GetApprovalAsync(string agentContextId, string approvalId, CancellationToken cancellationToken = default)
    {
        if (!_approvalMap.TryGetValue(agentContextId, out var approvalRecords) || approvalRecords.FirstOrDefault(a => a.Id == approvalId) is not { } approval)
        {
            throw new KeyNotFoundException($"Approval with ID '{approvalId}' not found.");
        }
        return Task.FromResult(approval);
    }

    public Task UpsertApprovalAsync(string agentContextId, string approvalId, ToolApprovalState approval, CancellationToken cancellationToken = default)
    {
        var existingApproval = _approvalMap.AddOrUpdate(agentContextId, _ => new List<ToolApprovalState> { approval }, (_, approvals) =>
        {
            var index = approvals.FindIndex(a => a.Id == approvalId);
            if (index != -1)
            {
                approvals[index] = approval;
            }
            else
            {
                approvals.Add(approval);
            }
            return approvals;
        });
        return Task.CompletedTask;
    }
}
