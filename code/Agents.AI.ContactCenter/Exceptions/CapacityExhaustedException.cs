using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Exceptions;

/// <summary>
/// Thrown by <see cref="IAgentTierResolver"/> when no <see cref="AgentTier"/>
/// in the configured fallback order can admit a new session — every enabled
/// tier at or below the active ceiling is already at its effective capacity.
/// </summary>
/// <remarks>
/// At the answer path this signals the call must be rejected per ADR-0004
/// (overflow / busy treatment is the caller's responsibility).
/// </remarks>
public sealed class CapacityExhaustedException : Exception
{
    public CapacityExhaustedException()
        : base("No agent tier in the configured fallback order has capacity to admit a new session.")
    {
    }

    public CapacityExhaustedException(string message) : base(message)
    {
    }

    public CapacityExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
