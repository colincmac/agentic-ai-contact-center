using Agents.AI.ContactCenter.State;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>Backing store selection for the per-call state plane.</summary>
public enum CallStateBackend
{
    /// <summary>In-process store. Suitable for dev, Aspire, single-pod, and the degraded-mode fallback.</summary>
    InMemory,

    /// <summary>Redis-backed store (hash snapshot + optional stream log). Requires a registered <c>IConnectionMultiplexer</c>.</summary>
    Redis,

    /// <summary>Azure Cosmos DB store (document snapshot + optional event container). Requires a registered <c>CosmosClient</c>.</summary>
    Cosmos
}

/// <summary>
/// Options for the provider-based call-state plane registered by <c>AddCallState</c>.
/// </summary>
public sealed class CallStateOptions
{
    public const string SectionName = "CallState";

    /// <summary>Which <see cref="ICallStateStore"/> implementation to wire. Defaults to <see cref="CallStateBackend.InMemory"/>.</summary>
    public CallStateBackend Backend { get; set; } = CallStateBackend.InMemory;

    /// <summary>When true, also wires an <see cref="ICallEventLog"/> for append-only audit/replay. Off by default.</summary>
    public bool EnableEventLog { get; set; } = false;

    /// <summary>
    /// Persist a fresh snapshot after at most this many folded events. The persister also flushes when
    /// the event batch drains, so this primarily bounds work under sustained event bursts.
    /// </summary>
    public int SnapshotEveryNEvents { get; set; } = 25;

    /// <summary>
    /// Maximum queued persistence events/flush barriers per call (one additional item may be in flight).
    /// The synchronous emit path never waits for storage: overflow rejects the event and faults the
    /// projector rather than dropping state. Size this for the expected burst and backend latency.
    /// </summary>
    public int PersistenceQueueCapacity { get; set; } = 1024;
    public int ObserverQueueCapacity { get; set; } = 256;

    /// <summary>
    /// Maximum time disposal waits for hydration and queued persistence to drain. On expiry the
    /// backend token is cancelled and disposal reports failure, even if the backend ignores cancellation.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Logical name of the Cosmos database when <see cref="Backend"/> is <see cref="CallStateBackend.Cosmos"/>.</summary>
    public string CosmosDatabaseName { get; set; } = "ContactCenter";

    /// <summary>Container holding per-call state snapshots (Cosmos backend).</summary>
    public string CosmosSnapshotContainerName { get; set; } = "CallState";

    /// <summary>Container holding the append-only event log (Cosmos backend, when the log is enabled).</summary>
    public string CosmosEventContainerName { get; set; } = "CallEvents";

    /// <summary>Optional time-to-live applied to persisted call state, after which the backend may evict it.</summary>
    public TimeSpan? Ttl { get; set; } = TimeSpan.FromHours(12);
}
