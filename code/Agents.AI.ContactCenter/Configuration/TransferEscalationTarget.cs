using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Destination the call-session container escalates to when a strategy decides the call
/// must be handed off to a live agent (caller asked for a representative, NLU classified
/// the well-known <c>transfer_to_agent</c> intent, etc.). Resolved per-call by strategies
/// that emit <see cref="OutboundDirective.TransferCall"/>.
/// </summary>
public sealed record TransferEscalationTarget(
    string TargetIdentifier,
    TransferKind Kind = TransferKind.BlindToPhoneNumber);
