using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Coordination;

namespace Agents.AI.ContactCenter.Exceptions;

/// <summary>
/// Thrown by <see cref="ICallSessionFactory.CreateAsync"/> when the call's
/// ownership row in the <see cref="ICallOwnershipDirectory"/> is already
/// held by another pod. The caller (typically the IncomingCall webhook
/// handler) should hand the request off to <see cref="IWebhookForwarder"/>
/// per ADR-0011 instead of processing locally.
/// </summary>
public sealed class CallOwnershipConflictException : InvalidOperationException
{
    public CallOwnershipConflictException(string callId, CallOwnership existingOwner)
        : base($"Call {callId} is already owned by cluster={existingOwner.ClusterId} pod={existingOwner.PodId} (instance={existingOwner.InstanceId})")
    {
        CallId = callId;
        ExistingOwner = existingOwner;
    }

    public string CallId { get; }

    public CallOwnership ExistingOwner { get; }
}
