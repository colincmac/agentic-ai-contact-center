namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Distinguishes streaming-mode (bi-di WS pinned to one pod) calls from
/// verb-mode (Call Automation REST) calls so the cross-pod callback router
/// in ADR-0011 can decide whether a non-owning pod must forward.
/// </summary>
public enum CallOwnershipKind
{
    /// <summary>
    /// Verb-mode call. State is fully in Redis; any pod in the same cluster
    /// can process callbacks for it without forwarding.
    /// </summary>
    Verb = 0,

    /// <summary>
    /// Streaming-mode call. The bi-di media WebSocket terminates on the
    /// owning pod; non-owning pods must forward mid-call events to it.
    /// </summary>
    Streaming = 1,
}

/// <summary>
/// Snapshot of which pod currently owns a call. Written to
/// <c>owner:{callConnectionId}</c> in Redis (ADR-0004 / ADR-0011).
/// </summary>
/// <param name="ClusterId">Cluster of the owning pod (ADR-0010).</param>
/// <param name="PodId">Kubernetes pod name of the owning pod.</param>
/// <param name="InstanceId">Per-process GUID of the owning pod incarnation.</param>
/// <param name="Kind">Whether the call is pod-pinned (streaming) or pod-agnostic (verb).</param>
/// <param name="LeaseUntil">Wall-clock instant after which the lease is reapable.</param>
public sealed record CallOwnership(
    string ClusterId,
    string PodId,
    string InstanceId,
    CallOwnershipKind Kind,
    DateTimeOffset LeaseUntil);

/// <summary>
/// Outcome of <see cref="ICallOwnershipDirectory.TryAcquireAsync"/>.
/// </summary>
/// <param name="Acquired">
/// <c>true</c> when the local pod is now the owner; <c>false</c> when another
/// pod already owns the call (in which case <see cref="Owner"/> describes it
/// so the caller can forward per ADR-0011).
/// </param>
/// <param name="Owner">The current owner — local pod when <see cref="Acquired"/> is true, otherwise the existing owner.</param>
public readonly record struct CallOwnershipAcquireResult(
    bool Acquired,
    CallOwnership Owner);
