using Azure.Communication;
using Azure.Communication.CallAutomation;
using Agents.AI.Monitoring.Dynamics;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Shared helpers for the ACS-backed <see cref="ICallControl"/> implementations.
/// </summary>
internal static class AcsCallControl
{
    /// <summary>
    /// Build the strongly typed ACS <see cref="TransferToParticipantOptions"/>
    /// for the given <see cref="TransferRequest"/>. The target identifier shape
    /// is selected based on <see cref="TransferRequest.Kind"/>.
    /// </summary>
    public static TransferToParticipantOptions BuildTransferOptions(TransferRequest request)
    {
        TransferToParticipantOptions options = request.Kind switch
        {
            TransferKind.BlindToPhoneNumber => new TransferToParticipantOptions(
                new PhoneNumberIdentifier(request.TargetIdentifier)),
            TransferKind.BlindToTeamsUser => new TransferToParticipantOptions(
                new MicrosoftTeamsUserIdentifier(request.TargetIdentifier)),
            TransferKind.Consultative => new TransferToParticipantOptions(
                new CommunicationUserIdentifier(request.TargetIdentifier)),
            _ => new TransferToParticipantOptions(
                new CommunicationUserIdentifier(request.TargetIdentifier))
        };

        if (request.CustomContext is { Count: > 0 } context)
        {
            options.OperationContext = string.Join(";", context.Select(kv => $"{kv.Key}={kv.Value}"));

            // Propagate the correlation payload to the transfer target (Dynamics 365) so the agent
            // gets a screen-pop and the call stays correlated across the hand-off. Same-tenant blind
            // transfer rides the Microsoft backbone as VoIP headers; Teams/consultative (potentially
            // cross-tenant / SBC) is a real SIP transfer, so only the compact context id goes in the UUI.
            var transport = request.Kind == TransferKind.BlindToPhoneNumber
                ? TransferTransport.Voip
                : TransferTransport.Sip;

            var contextId = context.TryGetValue(D365ContextVariableMap.ContextId, out var cid) ? cid : null;
            var headers = TransferContextHeaderBuilder.Build(context.ToArray(), transport, contextId);

            foreach (var header in headers.VoipHeaders)
            {
                options.CustomCallingContext.AddVoip(header.Key, header.Value);
            }

            if (headers.SipUui is not null)
            {
                options.CustomCallingContext.AddSipUui(headers.SipUui);
            }
        }

        return options;
    }
}
