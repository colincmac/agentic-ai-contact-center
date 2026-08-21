namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Redis key builders for the call-state plane. The <c>{callId}</c> hash tag colocates a call's
/// snapshot and event stream on the same cluster slot, so the optional cross-key operations stay local.
/// </summary>
internal static class CallStateRedisKeys
{
    /// <summary>Hash holding the per-call snapshot: one field per slice plus version/timestamp metadata.</summary>
    public static string Snapshot(string callId) => $"callstate:{{{callId}}}";

    /// <summary>Stream holding the per-call append-only event log.</summary>
    public static string EventStream(string callId) => $"callstate:{{{callId}}}:events";

    /// <summary>Counter backing the monotonic event sequence for a call.</summary>
    public static string EventSequence(string callId) => $"callstate:{{{callId}}}:seq";

    /// <summary>Hash field storing the snapshot version.</summary>
    public const string VersionField = "__version";

    /// <summary>Hash field storing the last-updated timestamp.</summary>
    public const string UpdatedAtField = "__updatedAt";

    /// <summary>Hash field storing the replay watermark (the last folded event sequence).</summary>
    public const string SequenceField = "__seq";
}
