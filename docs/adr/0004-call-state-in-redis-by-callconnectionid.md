# ADR-0004 — Distributed coordination plane in Redis keyed by `callConnectionId`; stateless pods; webhook idempotency via `(callConnectionId, sequenceNumber)`

- **Status:** Accepted
- **Date:** initial deployment; revised 2026-05-15 for hyperscale (hyperscale reqs across active-active clusters per [ADR-0010](0010-active-active-multi-cluster-topology.md))

## Context

ACS Call Automation callbacks (mid-call CloudEvents) and Event Grid `IncomingCall` events are delivered to internet-reachable HTTPS endpoints with **at-least-once** semantics — retries with exponential backoff and a default 24-hour TTL ([`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md)). The agent app runs as multiple stateless replicas behind a load balancer (per [ADR-0002](0002-acs-call-automation-as-control-plane.md), the app is just an HTTP service); any pod can receive any callback for any active call. At hyperscale that surface is multi-cluster — "any pod" means "any pod in the answering cluster" subject to the pod-affinity rules in [ADR-0011](0011-pod-ownership-and-lease-model.md).

For each in-flight call the app needs to remember:

- The current menu/dialog node (so a `RecognizeCompleted` knows which transition to apply).
- Per-node retry counts (for re-prompting policy — see [`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md)).
- Collected slot values (intent, account number, language, etc.) to pass downstream on `TransferCallToParticipant` as `customCallingContext`.
- The active degradation **tier** (per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) so a mid-call fallback can switch dialog modes without dropping the caller.
- Correlation identifiers (`correlationId`, `serverCallId`, `callConnectionId`) for telemetry.

In-process state is unsafe because the next callback may land on a different pod; sticky sessions are not guaranteed (Event Grid and Call Automation deliver to whichever replica the load balancer picks). Equally, callbacks may be **duplicated** by retries — applying a `RecognizeCompleted` twice would advance the menu past where the caller actually is.

The earlier revision of this ADR conflated four different concerns into one `callConnectionId`-keyed hash: dialog state, dedup, the active tier, and (implicitly) any cross-call coordination. In hyperscale scenarios, across active-active clusters those concerns have **different access patterns, different TTLs, different hot-key risk, and different sharding requirements**, and must be split.

## Decision

### Coordination plane

- **Store:** **Azure Managed Redis Enterprise** (cluster mode enabled) with **active geo-replication** between the cluster regions in [ADR-0010](0010-active-active-multi-cluster-topology.md). Single-shard / single-region Redis is rejected as the default at this scale — it cannot absorb the per-second op rate or survive a regional event. The repo's Aspire reference deployment provisions a single-region instance for dev; production overlays the Enterprise + geo-replication topology.
- **Pods are stateless.** No in-memory dialog state, no local file cache, no sticky-session requirement. Any replica can serve any callback (subject to [ADR-0011](0011-pod-ownership-and-lease-model.md) for streaming-mode pod-affinity).
- **`operationContext`** is set on **every** Play / Recognize / Transfer / Add/RemoveParticipant call and carries the menu node ID (and any other dispatch context the app needs). Callbacks dispatch on `operationContext` rather than inferring intent from event type alone. This is mandatory, not optional.

### Namespaces

Four namespaces with distinct policies. Per-call namespaces use **hash-tagging** so all keys for a given call colocate on the same shard:

| Namespace | Key | Op shape | TTL | Hash tag | Purpose |
|---|---|---|---|---|---|
| `state:{callConnectionId}` | hash (or JSON) | `HSET` / `HGETALL` | call duration + 4 h | `{callConnectionId}` | Dialog state: `menuNode`, `retryCount[node]`, `slots`, `tier`, `correlationId`, `serverCallId`, `lastSequenceNumber`, … |
| `dedup:{callConnectionId}:{sequenceNumber}` | string | `SET … NX EX` | 30 m (≥ longest plausible event-retry window) | `{callConnectionId}` | Idempotency token for at-least-once callback delivery. Hot under retry storms; deliberately short TTL. |
| `owner:{callConnectionId}` | hash with lease | `HSET` + heartbeat-renewed `EXPIRE` | renewed every 30 s, 90 s expiry | `{callConnectionId}` | Owning `(clusterId, podId, instanceId, kind, leaseUntil)` per [ADR-0011](0011-pod-ownership-and-lease-model.md). |
| `cap:tier:{tier}` | atomic counter | `INCR` / `DECR` (sharded) | TTL'd by lease, not call | **deliberately unkeyed** so it lands on a different shard from per-call keys | Global per-tier admission counter feeding `IDistributedCapacityTracker`. |
| `ceiling:cluster:{clusterId}` | string + Pub/Sub channel | `SET` + `PUBLISH` | until next change | per-cluster | Active tier ceiling per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md), broadcast to all pods in that cluster. |
| `pod:lease:{clusterId}:{podId}` | hash with lease | heartbeat-renewed | renewed every 30 s, 90 s expiry | per-cluster | Pod liveness signal consumed by the [ADR-0011](0011-pod-ownership-and-lease-model.md) reaper. |

Explicit purge of `state:*` and `owner:*` on `CallDisconnected`. `dedup:*` and `cap:tier:*` self-expire — never `KEYS` / `SCAN` to clean them up.

### Idempotency

Before applying any state transition, dedup on the tuple **`(callConnectionId, sequenceNumber)`** using `SET dedup:{callConnectionId}:{sequenceNumber} 1 NX EX 1800`. If the `SET` returns false the event has been (or is being) processed; the handler short-circuits and returns 200 OK to the publisher. The **forwarding pod** in [ADR-0011](0011-pod-ownership-and-lease-model.md)'s cross-pod dispatch performs the dedup check **before** forwarding, so a duplicate Event Grid delivery cannot duplicate the forwarded call.

### Degraded-mode admission

If the global Redis is unreachable from a pod's cluster (regional networking event, Redis Enterprise quorum loss):

1. The cluster's pods stop reading `cap:tier:*` and `ceiling:cluster:{clusterId}` and switch the local `IAgentTierResolver` to a **cluster-local mode**: it counts only this cluster's currently-active calls (via the in-pod `ICallSessionRegistry` aggregated through a fast cluster-local fan-out) and admits up to **the cluster's configured share of the global cap** (default 60–65 %, set per [ADR-0010](0010-active-active-multi-cluster-topology.md)).
2. New calls above the local share are refused via the [ADR-0003](0003-incomingcall-delivery-via-event-grid.md) circuit-breaker `RedirectCall` to the Tier 4 overflow target.
3. Existing calls continue. Dedup falls back to a per-pod in-memory LRU sized for the per-pod event rate — duplicates **may** be reprocessed within the cluster degradation window, which is acceptable for the rare regional Redis outage case.
4. When global Redis comes back, pods resume normal admission and the in-memory dedup LRU is dropped.

This fallback is the **circuit-breaker** referenced in [ADR-0003](0003-incomingcall-delivery-via-event-grid.md) — without it, a Redis brownout becomes a global outage.

### Hot-key avoidance

- `cap:tier:*` is implemented as a **sharded counter** (per-cluster local counter periodically rolled up to the global counter) the moment a single counter shows up as a measurable bottleneck. `IDistributedCapacityTracker` hides the split; callers see a single read.
- `dedup:*` is per-call-per-event-id, so no single key is hot. The bulk write rate is high; the Redis Enterprise cluster is sized for this rather than relying on key-locality optimizations.
- `state:*` is hot per call but not across calls. Hash-tagging keeps reads/writes for one call on one shard so cross-shard transactions are unnecessary.

## Consequences

- Horizontal scaling is straightforward: add replicas, no rebalancing required. Rolling deployments do not interrupt in-flight verb-mode calls because the state lives in Redis. Streaming-mode calls are subject to [ADR-0011](0011-pod-ownership-and-lease-model.md)'s drain-and-reroute model.
- Redis is on the **hot path** for every callback. Its availability and p99 latency directly bound call-handling latency, so it must be sized for the full callback rate (multiply concurrent calls by typical events per call) and protected with Polly-style retries plus the degraded-mode admission contract above. The Aspire reference deployment uses Azure Cache for Redis (single-region) for dev; production overlays Azure Managed Redis Enterprise with active geo-replication per [ADR-0010](0010-active-active-multi-cluster-topology.md).
- Webhook handlers are **idempotent by construction**: if the dedup check finds the `(callConnectionId, sequenceNumber)` already processed, the handler short-circuits. This is the correctness contract that makes ADR-0003's at-least-once delivery safe across both single-pod and cross-pod-forwarded delivery paths.
- Mid-call degradation ([ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) reads the cluster `ceiling:cluster:*` namespace (cached per pod with Pub/Sub invalidation) and writes the active `tier` to `state:{callConnectionId}`. A failed realtime turn flips the tier and the next callback continues in the new mode using the same `menuNode` — no caller-visible drop.
- Capacity admission for new calls reads and increments `cap:tier:*` atomically. The `IAgentTierResolver` implementation in `Agents.AI.ContactCenter` is the single source of admission truth; bypassing it (e.g., constructing `CallSession` directly) is a misuse and breaks global capacity.
- Custom calling context for escalations (intent, digits, language) is sourced from the same `slots` field on `state:*`, so the screen-pop payload sent to Dynamics CCaaS on `TransferCallToParticipant` is exactly what the IVR collected.
- Telemetry must include `correlationId`, `serverCallId`, `callConnectionId`, `operationContext`, `sequenceNumber`, **`clusterId`**, and **`podId`** on every line (the last two are added by this revision so cross-pod forwarding events are traceable).
- The framework owns the namespaces above. Application code must go through `IWebhookIdempotencyStore`, `ICallOwnershipDirectory`, `IDistributedCapacityTracker`, and `ITierCeilingProvider` rather than touching Redis directly, so the hash-tagging, sharding, and degraded-mode contracts stay enforceable.

## Alternatives considered

- **In-memory state with sticky sessions.** Rejected. Event Grid and Call Automation do not honour load-balancer affinity; even if they did, a pod restart during a call would lose all state for active calls on that pod.
- **Cosmos DB as the state store.** Rejected for the per-call state. Cosmos works, but Redis offers an order-of-magnitude lower per-operation latency at this access pattern (small documents, very high read/write rate, short lifetime). Cosmos remains the right store for *durable* call records (CDRs, transcripts, biometrics enrolments) — those are written **once**, after the call ends, and are out of scope for this ADR.
- **One hash per call carrying everything (the original revision).** Rejected at hyperscale. Conflates four namespaces with different TTL, hot-key, and sharding requirements; forces every namespace onto the same shard as `state:*`; makes the dedup and capacity counters pay the per-call hash overhead and forces them to share an eviction policy that fits none of them well. The four-namespace split above is what this ADR now mandates.
- **Single-region Redis at production scale.** Rejected. Single-region Redis is the default in dev and acceptable up to small-cluster scale, but cannot survive an Azure region event and cannot deliver the cross-cluster admission view that [ADR-0010](0010-active-active-multi-cluster-topology.md) requires. Production runs Azure Managed Redis Enterprise with active geo-replication.
- **Per-pod sharding by `callConnectionId` hash.** Rejected. Adds a routing layer and reintroduces the rolling-deployment hazard. The "any pod can serve any callback" model is simpler and matches how ACS actually delivers events; cross-pod streaming dispatch lives in [ADR-0011](0011-pod-ownership-and-lease-model.md) where it belongs.
- **Skipping dedup and relying on idempotent operations only.** Rejected. State transitions like "advance to next menu node" or "increment retryCount" are not naturally idempotent; the explicit `(callConnectionId, sequenceNumber)` dedup is the simplest correct solution.
- **Using `correlationId` instead of `callConnectionId` as the key.** Rejected. `callConnectionId` is the identifier ACS Call Automation uses on every mid-call event; `correlationId` is best for cross-system support traces. They serve different purposes — both are stored, only `callConnectionId` is the primary key.
- **Per-tier capacity counter as a single global INCR.** Acceptable as a v1; mandated to be **sharded** (per-cluster local counter rolled up to a global view) once a single counter shows up as a measurable bottleneck. The `IDistributedCapacityTracker` abstraction lets the split land without changing callers.

## Related

- [ADR-0003](0003-incomingcall-delivery-via-event-grid.md) — at-least-once delivery and the circuit-breaker `RedirectCall` this ADR's degraded-mode admission feeds.
- [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) — Tier ceiling broadcast on `ceiling:cluster:*` and the global `cap:tier:*` soft/hard caps.
- [ADR-0010](0010-active-active-multi-cluster-topology.md) — Active-active topology and the geo-replicated Redis Enterprise deployment this ADR depends on.
- [ADR-0011](0011-pod-ownership-and-lease-model.md) — Pod and call leases on `pod:lease:*` and `owner:*`.
