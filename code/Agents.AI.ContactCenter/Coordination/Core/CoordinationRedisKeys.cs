namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Hash-tagged Redis key formats for the coordination plane (ADR-0004).
/// The literal <c>{callConnectionId}</c> braces colocate every per-call key
/// on the same shard so cross-namespace operations for one call stay local.
/// Cross-call counters deliberately omit the tag so they spread across shards.
/// </summary>
internal static class CoordinationRedisKeys
{
    /// <summary>
    /// <c>dedup:{callConnectionId}:sequenceNumber</c> — webhook idempotency
    /// token; <c>SET … NX EX</c> with <c>WebhookIdempotencyOptions.TokenLifetime</c>.
    /// </summary>
    public static string Dedup(string callConnectionId, int sequenceNumber)
        => $"dedup:{{{callConnectionId}}}:{sequenceNumber}";

    /// <summary>
    /// <c>owner:{callConnectionId}</c> — call ownership lease per ADR-0011.
    /// Value is the <see cref="CallOwnershipCodec"/>-encoded
    /// <see cref="CallOwnership"/>; TTL matches
    /// <c>CallOwnershipOptions.LeaseDuration</c>.
    /// </summary>
    public static string Ownership(string callConnectionId)
        => $"owner:{{{callConnectionId}}}";

    /// <summary>
    /// <c>incoming:{serverCallId}</c> — admission state for the one pod allowed to invoke
    /// ACS AnswerCall for a server call id.
    /// </summary>
    public static string IncomingCallAdmission(string serverCallId)
        => $"incoming:{{{serverCallId}}}";

    /// <summary>
    /// <c>ceiling:cluster:{clusterId}</c> — active tier ceiling for the cluster
    /// per ADR-0008. Used both as the string key (<c>SET</c> / <c>GET</c>) and
    /// as the Pub/Sub channel name; the published message body is the
    /// <c>(int)</c>-coded <see cref="Configuration.AgentTier"/>.
    /// </summary>
    public static string ClusterTierCeiling(string clusterId)
        => $"ceiling:cluster:{{{clusterId}}}";

    /// <summary>
    /// <c>cap:tier:{tier}</c> — global per-tier admission counter per
    /// ADR-0004. The literal-brace hash tag is the integer tier code so each
    /// tier's counter lands on its own Redis shard, deliberately distinct
    /// from per-call <c>{callConnectionId}</c> keys. Operations are
    /// <c>INCR</c> / <c>DECR</c> via the
    /// <see cref="IDistributedCapacityTracker"/> Lua scripts.
    /// </summary>
    public static string CapacityCounter(Configuration.AgentTier tier)
        => $"cap:tier:{{{(int)tier}}}";

    /// <summary>
    /// <c>pod:lease:{clusterId}:{podId}</c> — pod heartbeat lease per
    /// ADR-0011. The hash tag on <c>{clusterId}</c> keeps every pod lease
    /// for one cluster on the same shard so the reaper's cluster-scoped
    /// scans stay local. The value is the local
    /// <see cref="IClusterIdentity.InstanceId"/>; TTL is
    /// <c>PodHeartbeatOptions.LeaseDuration</c>.
    /// </summary>
    public static string PodLease(string clusterId, string podId)
        => $"pod:lease:{{{clusterId}}}:{podId}";
}
