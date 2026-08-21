using Agents.AI.ContactCenter.Calling.Core;
using Azure.Communication.CallAutomation;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Tiny adapter over the bits of <see cref="CallConnection"/> that
/// <see cref="AcsCallAutomationEdge"/> needs for call-control verbs
/// (hang up, transfer). Exists so tests can drive the verb edge without
/// standing up a real ACS call connection.
/// </summary>
public interface ICallControlClient
{
    Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken);
    Task TransferAsync(TransferToParticipantOptions options, CancellationToken cancellationToken);
}
