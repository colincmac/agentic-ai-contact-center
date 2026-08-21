# ADR-0010 — Active-active multi-cluster topology for hyperscale (100k+ concurrent callers)

- **Status:** Accepted
- **Date:** 2026-05-15

## Context

For contact centers we'll need to target hyperscale level requirements, with regional resilience. The earlier ADRs ([ADR-0002](0002-acs-call-automation-as-control-plane.md), [ADR-0003](0003-incomingcall-delivery-via-event-grid.md), [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md), [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)) implicitly assumed a single AKS cluster behind a single Redis instance. That topology does not survive an Azure region event and does not scale linearly past the limits of a single AKS control plane / single Redis shard.

The constraints that drive the topology decision are:

- **ACS Call Automation per-resource throughput.** A single ACS resource has documented limits on concurrent calls and per-second event publication. At hyperscale level the platform is comfortably past the safe ceiling of any one ACS resource and any one Event Grid system topic.
- **Event Grid webhook delivery rate.** A single webhook endpoint per cluster will absorb the answer-path event rate (`IncomingCall`) plus the much larger mid-call event rate (`Recognize*`, `Play*`, `CallTransfer*`, `CallDisconnected`, …). At hyperscale this is hundreds of events per second per cluster sustained, with bursts during incidents.
- **Realtime AI session ceilings ([ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md)).** Realtime model providers cap concurrent sessions per region/subscription. Hyperscale concurrent realtime is not achievable in any single region and should not be the design target — graceful degradation per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) supplies the rest of the capacity.
- **Bi-di WebSocket pinning ([ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md)).** Streaming-mode calls are anchored to the AKS pod that holds the WS. A whole-region failure must not strand half of those calls invisibly.
- **Stateless-pod contract ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)).** Mid-call callbacks may land on any pod. At a multi-cluster scale "any pod" must include "any pod in any cluster", subject to the pod-affinity rules in [ADR-0011](0011-pod-ownership-and-lease-model.md).

Single-cluster scaling (one AKS, one Redis, one ACS resource) is not a credible path to hyperscale scenarios and is rejected without further analysis.

## Decision

### Topology

- **Two or more active-active AKS clusters** in different Azure regions. Each cluster is sized for **~60–65 % of the total target** so that a single-cluster outage can be absorbed by the survivors with documented degradation per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md). For the hyperscale level, that is two clusters at ~200k each, or three clusters at ~135k each.
- **One ACS resource per cluster** (co-located in the cluster's region), each with its own Event Grid system topic and its own webhook endpoint inside the cluster. ACS resources are not shared between clusters.
- **Front Door** (or Traffic Manager — Front Door is the default) fronts the answer-path webhooks for *outbound dial-out, dashboard, supervisor, and admin* surfaces. The `IncomingCall` Event Grid subscription on each ACS resource targets its **own cluster's webhook directly**, not Front Door — Event Grid → Front Door → cluster adds a hop and a TLS termination on the answer path that ADR-0003 explicitly budgets against.
- **Calls do not migrate between clusters mid-call.** Once a call has been answered in a cluster, it lives and dies in that cluster. Cross-cluster failover applies only to *new* calls. Mid-call cluster failure is handled by [ADR-0011](0011-pod-ownership-and-lease-model.md)'s reaper, which performs a polite, audible hangup or external-PSTN re-route — never a silent drop and never a cross-cluster session migration.

### Coordination plane

- **One logical Redis** spans the cluster fleet, provided by **Azure Managed Redis Enterprise** with **active geo-replication** between the cluster regions. Enterprise tier gives the cluster mode (sharding) needed to absorb the per-second op rate, and active geo-replication gives both clusters a consistent view of the namespaces in [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md).
- **Hash-tagging** is mandatory on per-call keys so that all keys for a single call land on the same shard (`state:{callConnectionId}`, `dedup:{callConnectionId}:…`, `owner:{callConnectionId}`). Cross-call counters (`cap:tier:*`, `ceiling:cluster:*`) deliberately live on different shards to spread the hot-key load.
- **Per-tier capacity counters are global.** Both clusters consume from and report to the same `cap:tier:*` counter so that admission honors the tier ceilings in [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) across the whole platform, not per cluster.
- **Tier ceiling is per cluster.** A regional realtime AI outage in cluster A should cap A at Tier 2 without dragging cluster B down. The ceiling broadcast is on the `ceiling:cluster:{clusterId}` namespace; pods only react to their own cluster's ceiling.

### Pod and cluster identity

- Every pod has a stable **`(clusterId, podId, instanceId)`** identity surfaced through `IClusterIdentity`. `clusterId` is supplied by environment variable (set by the Helm chart / deployment manifest); `podId` is the Kubernetes pod name; `instanceId` is a fresh GUID per process start so pod restarts don't reuse leases.
- Pod identity is the **lease principal** for [ADR-0011](0011-pod-ownership-and-lease-model.md)'s ownership and heartbeat model.

### Failure modes and admission

- If global Redis is unreachable from a cluster (regional networking event), the cluster falls back to **cluster-local admission**: it counts only its own active calls against the per-tier cap and refuses to admit new calls above its local share (60–65 %). Existing calls continue. This is documented in [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)'s degraded-mode contract.
- If an ACS resource degrades, Front Door / Traffic Manager removes that cluster's *outbound* surfaces from rotation. The Event Grid `IncomingCall` subscription stays bound to the ACS resource — there is no rerouting path for `IncomingCall` events once published, and ADR-0003's dead-letter queue captures undeliverable events for reconciliation.

## Consequences

- **Per-cluster sizing is a deployment-planning concern, not a code concern.** The framework treats every cluster identically; the only per-cluster knob is the cluster identity and the fallback-admission share.
- **The coordination plane is on the hot path for every call.** Redis Enterprise geo-replication latency (typically tens of milliseconds intra-region, low hundreds intra-continent) becomes part of the answer-path budget. Pods cache ceiling and tier-resolver state locally and refresh asynchronously to keep the answer path off the wire.
- **Cross-cluster active-active does not give cross-cluster mid-call failover.** This is a deliberate constraint, not a defect. The cost of session migration mid-call (state transfer, WS re-binding, ACS call re-attachment) outweighs the benefit; the audible-hangup reaper in [ADR-0011](0011-pod-ownership-and-lease-model.md) is the correct UX for the rare cluster-loss case.
- **Per-tier global counters are a hot key.** The framework uses a sharded counter pattern (per-cluster local counters with periodic rollup to the global counter) once a single counter becomes a measurable bottleneck. The split is invisible to callers of `IDistributedCapacityTracker`.
- **Each cluster needs its own Cognitive Services (TTS/ASR) link on its ACS resource.** Bi-di media streaming is per-cluster by ADR-0007; cross-region media is not in scope.
- **Cost model is multiplied by cluster count.** Two clusters double the AKS, ACS, Cognitive Services, and Application Insights line items; Redis Enterprise geo-replication is one resource billed per replica region. The headline cost shape is "2× compute + 1× geo-replicated Redis", not "2× everything".
- **Synthetic call probers run per cluster.** Per ADR-0003 / `runbooks/timing-and-retries.md`, each cluster needs its own end-to-end synthetic that hits its own ACS resource, so a TPE-binding regression in one region is caught even when the other region is healthy.

## Alternatives considered

- **Single AKS cluster with per-zone autoscaling.** Rejected. Single point of failure for an Azure region event; single ACS resource limit; single Event Grid topic limit. Workable up to maybe 50–75k concurrent on aggressive sizing; not 100k+.
- **Active-passive across regions.** Rejected. The passive cluster is a cost center that, by design, is unproven on the day it is needed. Active-active continuously exercises the coordination plane and the failover path.
- **N>2 small clusters (e.g., five at 65k each).** Acceptable and supported by this ADR. Operational overhead (separate Helm releases, separate ACS resources, separate Cognitive Services links per cluster) grows linearly; only adopt if a per-cluster ceiling forces it. The default is **two**.
- **Cross-cluster session migration mid-call.** Rejected. Requires WS state transfer, ACS call re-attachment, and is incompatible with [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md)'s bi-di pinning. Audible re-route per [ADR-0011](0011-pod-ownership-and-lease-model.md) is the supported behavior on cluster loss.
- **Globally distributed Redis (e.g., Cosmos DB for a key-value workload).** Rejected for the per-call hot path. Cosmos latency on small per-call ops is an order of magnitude higher than Redis for this access pattern; Redis Enterprise active geo-replication delivers the consistency that this workload actually needs (own-cluster reads, cross-cluster writes are eventually consistent and the ownership model in ADR-0011 tolerates the convergence window). Cosmos remains the right store for **durable** call records (CDRs, transcripts, biometrics enrolments).
- **Sticky-session load balancing on the answer path** to keep callbacks on a single cluster's pods. Rejected for the same reason as in [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md): Event Grid does not honor LB affinity; pod restarts during a call would still land callbacks elsewhere.

## Related

- [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) — Redis namespaces, hash-tagging, and degraded-mode admission contract this ADR depends on.
- [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) — Tier ceiling broadcast and the cluster-scoped tier ceiling decision.
- [ADR-0011](0011-pod-ownership-and-lease-model.md) — Per-pod heartbeat and per-call ownership lease that operate inside this topology.
- [`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md) — Per-cluster synthetic prober and dead-letter requirements.
