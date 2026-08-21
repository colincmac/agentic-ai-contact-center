namespace Agents.AI.ContactCenter.Authorization.VoiceApproval;

public sealed class PendingVoiceApproval
{
    public string ToolName { get; set; } = string.Empty;
    public string? ApprovalMessage { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public bool IsApproved { get; set; } = false;
}

/// <summary>
/// Simple in-memory store for voice approvals keyed by participant id and tool name.
/// </summary>
public sealed class VoiceApprovalStore
{
    private readonly Dictionary<string, Dictionary<string, PendingVoiceApproval>> _approvals = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void SetPendingApproval(string participantId, string toolName, string? message)
    {
        _lock.Wait();
        try
        {
            if (!_approvals.ContainsKey(participantId))
            {
                _approvals[participantId] = new Dictionary<string, PendingVoiceApproval>();
            }

            _approvals[participantId][toolName] = new PendingVoiceApproval
            {
                ToolName = toolName,
                ApprovalMessage = message,
                RequestedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public PendingVoiceApproval? GetPendingApproval(string participantId, string toolName)
    {
        _lock.Wait();
        try
        {
            if (_approvals.TryGetValue(participantId, out var map) && map.TryGetValue(toolName, out var approval))
            {
                return approval;
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyCollection<PendingVoiceApproval> GetPendingApprovals(string participantId)
    {
        _lock.Wait();
        try
        {
            if (_approvals.TryGetValue(participantId, out var map))
            {
                return map.Values.ToList();
            }
            return Array.Empty<PendingVoiceApproval>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void GrantApproval(string participantId, string toolName)
    {
        _lock.Wait();
        try
        {
            if (_approvals.TryGetValue(participantId, out var map) && map.TryGetValue(toolName, out var approval))
            {
                approval.IsApproved = true;
                approval.RespondedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void DenyApproval(string participantId, string toolName)
    {
        _lock.Wait();
        try
        {
            if (_approvals.TryGetValue(participantId, out var map) && map.TryGetValue(toolName, out var approval))
            {
                approval.IsApproved = false;
                approval.RespondedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void ClearApproval(string participantId, string toolName)
    {
        _lock.Wait();
        try
        {
            if (_approvals.TryGetValue(participantId, out var map))
            {
                map.Remove(toolName);
                if (map.Count == 0)
                {
                    _approvals.Remove(participantId);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
