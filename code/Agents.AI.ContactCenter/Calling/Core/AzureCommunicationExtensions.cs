using System;
using System.Collections.Generic;
using System.Text;
using Azure.Communication.CallAutomation;
using Azure.Messaging.EventGrid.SystemEvents;

namespace Agents.AI.ContactCenter.Calling.Core;

public static class AzureCommunicationExtensions
{
    public static IncomingCallContext ToCallInfo(this AcsIncomingCallEventData e) => new()
    {
        CallId = e.ServerCallId,
        CallerIdentifier = e.FromCommunicationIdentifier.RawId,
        CallerDisplayName = e.CallerDisplayName,
        CorrelationId = e.CorrelationId,
        CallTargetIdentifier = e.ToCommunicationIdentifier.RawId
    };

}
