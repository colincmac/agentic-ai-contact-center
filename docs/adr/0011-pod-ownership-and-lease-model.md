# ADR-0011 — Pod ownership and lease model for callbacks under multi-cluster scale

- **Status:** Accepted
- **Date:** 2026-05-15

## Context

Two facts from earlier ADRs interact in a way that needs an explicit policy at hyperscale:

1. **Mid-call ACS callbacks are pod-agnostic by delivery.** [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) declares the agent app's pods stateless and any callback may land on any pod. Per-call dialog state lives in Redis, keyed by `callConnectionId`.
2. **Bi-di media-streaming WebSockets are pod-pinned by construction.** [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) consumes streaming-mode caller audio (and DTMF) on a WS that terminates on exactly one pod. That pod is the **only** pod that can write audio back to the caller for that call.

These are compatible for verb-mode (Tier 3 / Tier 4) calls — any pod can handle any callback because there is no pod-resident audio path. They are **not** automatically compatible for streaming-mode calls (Tier 0–2): a `RecognizeCompleted` arriving at pod B for a call whose WS lives on pod A cannot directly drive a barge-in on the audio path; pod B can read state but cannot speak to the caller.

[ADR-0010](0010-active-active-multi-cluster-topology.md) adds a second axis: pods belong to one of N active-active clusters, and the answering cluster owns the call for its lifetime. Cross-cluster callbacks for a given call are **not** expected (each ACS resource publishes only to its own cluster's webhook), but cross-pod callbacks within a cluster are routine.

The remaining concerns are:

- **How does the receiving pod find the WS-owning pod?**
- **What happens when the WS-owning pod dies mid-call?**
- **How is pod liveness signaled separately from call ownership, so a brief pod restart does not orphan thousands of calls?**

## Decision

### Two-level lease

- **Pod lease.** Every pod publishes a heartbeat to `pod:lease:{clusterId}:{podId}` every **30 s** with a **90 s TTL**. The lease value carries the pod's `instanceId` (per-process GUID — see [ADR-0010](0010-active-active-multi-cluster-topology.md)) so a pod restart is observable as an `instanceId` change without churning the pod identity.
- **Call ownership.** When a pod accepts ownership of a call (the WS handler builds the `AcsCallerEdge` for streaming, or the verb-mode handler builds the `AcsCallAutomationEdge`), it writes `owner:{callConnectionId}` carrying `{clusterId, podId, instanceId, kind, leaseUntil}` where `kind ∈ {streaming, verb}` and `leaseUntil` is renewed by the same pod heartbeat. The default `leaseUntil` is **90 s** ahead, refreshed every 30 s along with the pod lease.
- **One owner per call.** Ownership writes use `SET … NX` to defend against split-brain during an incident. Conflicts (rare; should only occur if a pod refuses to release after `EndAsync`) are resolved in favor of the existing owner; the new pod logs and rejects the call.

### Callback dispatch

Receiving pod looks up `owner:{callConnectionId}` for every incoming mid-call event. The branching is small:

| Event class | Owner is local pod | Owner is remote pod (same cluster) | Owner is unknown / lease expired |
|---|---|---|---|
| Verb-mode events (`RecognizeCompleted/Failed`, `PlayCompleted/Failed`, `CallTransferAccepted/Failed`) on a `kind = verb` call | Process locally. | **Process locally** — verb-mode state is fully in Redis; no pod-resident dependency. | Reaper path — see below. |
| Streaming-mode events (same shapes) on a `kind = streaming` call | Process locally. | **Forward** via internal HTTP to the owner pod's `/automation/callbacks/forward` endpoint. The forwarding pod returns 200 to ACS once the owner acks; if the owner is unreachable inside the answer-window SLA ([ADR-0003](0003-incomingcall-delivery-via-event-grid.md)), the receiving pod drops to the reaper path. | Reaper path — see below. |
| `CallDisconnected` (any `kind`) | Process locally and `DEL owner:{callConnectionId}`. | Process locally and `DEL owner:{callConnectionId}` — termination is naturally idempotent and does not require the WS to still exist. | Process locally as a no-op (the call is already gone). |

Cross-pod forwarding uses **internal cluster DNS** (`http://{podId}.{headlessSvc}.{namespace}.svc.cluster.local`) and a small Polly-wrapped `HttpClient`. The forward path is a **small fraction** of total callback traffic — only streaming-mode mid-call events that did not land on the WS-owning pod. The forwarding pod still does the dedup check from [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) before forwarding, so a duplicate Event Grid delivery does not duplicate the forwarded call.

Cross-cluster forwarding is **not** implemented. By [ADR-0010](0010-active-active-multi-cluster-topology.md), each cluster's ACS resource publishes only to its own cluster's webhook; a callback for a call owned by another cluster should be impossible. If observed (misconfiguration), the receiving pod logs and drops to the reaper path rather than attempting cross-cluster transport.

### Reaper

A per-pod `IPodHeartbeat` background service:

1. Renews the local pod lease and every owned-call lease at the 30 s tick.
2. Scans `owner:*` for entries whose `leaseUntil` is in the past **and** whose `pod:lease:{clusterId}:{podId}` is also expired (a fresh `instanceId` would have written a new lease). Matched entries are reaped:
   - **Verb-mode** orphans are simply re-bound to the reaper pod by overwriting `owner:*` (the dialog state is in Redis, no audio path is pod-resident, ownership transfer is free).
   - **Streaming-mode** orphans get a polite, audible reroute on the next ACS event for the call: the reaper pod calls `TransferCallToParticipant` to the configured overflow target (per [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) Tier 4) or, if no overflow target is configured, `Play(FileSource)` of an apology + `HangUp`. The caller is never silently dropped.
3. Reap claims are atomic (`SET … XX` against the expired lease value) so two reaper pods racing for the same orphan do not double-process.

### Graceful drain

On `SIGTERM`:

1. Pod stops accepting new calls (reports unhealthy on the *answer-path* readiness probe but stays healthy on the *callback* probe so in-flight calls keep being served).
2. Pod releases its `owner:*` leases for **verb-mode** calls — they are immediately reapable by another pod and traffic continues normally.
3. Pod retains its `owner:*` leases for **streaming-mode** calls until the call ends naturally or until a configurable drain timeout (default **5 minutes**) elapses, after which the streaming reroute path runs locally before the pod exits.
4. Pod stops the heartbeat last so the reaper does not race with shutdown.

## Consequences

- **The framework provides one new background service (`IPodHeartbeat`), one new HTTP endpoint (`/automation/callbacks/forward`), and one new lookup on the callback hot path.** The lookup is cached for the call's lifetime in the receiving pod once observed, so steady-state cost is one Redis `HGET` per call (not per event).
- **Streaming-mode call density per pod is bounded by WS count, not by callback rate.** A pod that owns 500 streaming calls handles its own callbacks plus its share of forwarded callbacks for those calls. Cluster-level capacity planning in [ADR-0010](0010-active-active-multi-cluster-topology.md) treats streaming-mode density as a per-pod constraint.
- **Pod restarts are visible to the reaper but invisible to in-flight verb-mode calls.** Verb-mode calls survive any single-pod outage because no pod resource is required to keep them alive.
- **Streaming-mode calls do not survive their pod.** This is the same constraint a self-hosted SIP/RTP stack would have and is the correct trade for the latency benefit of bi-di WS. The reaper makes the failure mode audible and bounded rather than silent.
- **Drain timeout caps deployment frequency for streaming-heavy fleets.** A rolling deploy that cycles every pod takes drain × pod-count / parallelism wall time. The default 5 minutes is tunable per cluster.
- **Cross-pod forwarding adds an internal HTTP hop on a small slice of callbacks.** The hop cost is bounded (intra-cluster) and is well under the answer-path SLA from [ADR-0003](0003-incomingcall-delivery-via-event-grid.md), but it is real and shows up in callback p95 dashboards.

## Alternatives considered

- **Cookie / sticky-session load balancing on the callback webhook.** Rejected. Event Grid does not present cookies; ACS does not honor LB affinity on callback delivery. The lookup-and-forward model is the only correct path.
- **Always process callbacks on the receiving pod (no forwarding).** Rejected for streaming-mode. Without the WS the pod cannot drive barge-in, cannot push synthesized audio to the caller, and cannot honor `interruptPrompt` semantics. State-only processing (writing to Redis, emitting telemetry) is fine but insufficient.
- **One-level lease (call only, no pod lease).** Rejected. The pod heartbeat is also the readiness signal for cluster-level capacity tracking ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) `cap:tier:*`). Without a separate pod lease, capacity counters drift on graceful pod replacement.
- **Cross-cluster forwarding for streaming calls.** Rejected — cross-region WS forwarding adds a media-quality hit and a TLS hop on the audio path, and the misconfiguration that would make it necessary is itself a bug worth surfacing rather than tolerating.
- **Reaper takes over streaming calls by re-establishing the WS.** Rejected. ACS does not expose "rebind the bi-di WS to a different endpoint" for an answered call; the call would have to be redirected at the ACS layer, which is a different operation with different semantics. The audible reroute is simpler and honest.
- **Per-pod call cap enforced only by Kubernetes limits.** Rejected. Kubernetes limits are a memory / CPU concern; streaming WS density wants an explicit count enforced by the framework so admission can refuse cleanly with a `RedirectCall` to the overflow target.

## Related

- [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) — Redis namespaces (`owner:*`, `pod:lease:*`, `cap:tier:*`, `dedup:*`) this ADR uses.
- [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) — bi-di WS pod-pinning consequence this ADR depends on.
- [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) — Tier 4 overflow target the reaper falls back to.
- [ADR-0010](0010-active-active-multi-cluster-topology.md) — Cluster identity and cross-cluster scope rules.
- [`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md) — Webhook handler SLA the forwarding hop must respect.
