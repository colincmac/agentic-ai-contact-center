using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling;

public sealed record CallSessionRequest
{
    /// <summary>Identity + routing facts. The canonical call id is <see cref="IncomingCallContext.CallId"/>.</summary>
    public required IncomingCallContext CallContext { get; init; }

    /// <summary>Override tier resolution. When null, the registered <c>IAgentTierResolver</c> picks (falls back to DtmfOnly).</summary>
    public AgentTier? PreferredTier { get; init; }

    /// <summary>Override workflow. When null, the strategy's default / single registered workflow is used.</summary>
    public string? WorkflowId { get; init; }
}

/// <summary>Immutable routing inputs and the tier ultimately admitted for a published session.</summary>
public sealed record CallSessionRouting(
    string? WorkflowId,
    AgentTier? PreferredTier,
    AgentTier AdmittedTier);
