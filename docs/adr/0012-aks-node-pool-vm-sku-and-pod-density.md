# ADR-0012 — AKS node-pool VM SKU and per-pod density for `voice-edge`

- **Status:** Accepted
- **Date:** 2026-05-21

> Imported decision record. Source-code references below name historical paths
> from the source showcase; the implementation is not yet present in this
> repository. No evidence record currently supports the performance figures,
> so treat them as sizing assumptions pending a reproducible load test.

## Context

The deployment view in [`aks-topology.md`](../architecture/aks-topology.md) names two node pools (`cpu` and `gpu-small`) and asserts a target of "~1k concurrent WS per `voice-edge` pod" but does not pick a specific VM SKU, justify the per-pod resource shape, or document how the per-pod density translates into nodes per cluster at the three scaling tiers we plan against (50k, 100k, up to 350k concurrent calls). This ADR closes that gap for the **CPU** node pool that runs `voice-edge`. The GPU pool that runs `intent-agent` and `voice-biometrics` is out of scope here — the model-selection ADR will own those VMs.

The forces that drive the SKU choice are all per-call:

- **Two WebSockets per call.** `voice-edge` terminates the ACS bidi media WS (`AcsCallerStreamEdge.cs`) and also holds one upstream WSS to the chosen Azure-hosted realtime / chat-completions backend (`Program.cs`). Pod density and per-pod socket limits are sized against the sum, not against ACS alone.
- **Bidirectional PCM at 16 kHz / 16-bit / mono on both legs.** `CallEdgeMetadata` defaults to `Pcm16Khz16BitMono` on inbound and outbound (`ICallEdge.cs`). Raw rate is 256 kbps per direction per WS; with the ACS JSON envelope, WS framing, and the upstream provider's JSON envelope the imported sizing assumption is ~1.3 Mbps aggregate per active call.
- **Bounded in-pod audio channels.** `AcsCallerStreamEdge` holds a 500-frame bounded inbound channel and a 500-frame bounded outbound channel, both `DropOldest`, plus a 64 KB pooled receive buffer per WS. Per-call sustained working set is 3–4 MB, dominated by the WS framing buffers and the per-call scoped DI graph (DropOldest keeps the channels from acting as a leak under backpressure).
- **CPU is I/O-bound, not inference-bound.** STT, TTS, and the realtime model are all Azure-hosted ([`aks-topology.md`](../architecture/aks-topology.md) "In-process vs gRPC"); SLM intent classification is offloaded to the GPU pool (historical `Program.cs` `intentagent` wiring). `voice-edge` CPU is spent on WS frame parsing, JSON envelope decode every 20 ms, VAD/presence, and `System.Threading.Channels` pumping — the imported sizing assumptions are ~2 mCPU/call sustained and ~5 mCPU/call peak during agent speech bursts.
- **One persistent upstream WS per call to a small set of provider IPs.** This creates the SNAT-port pressure that has historically broken multi-thousand-WS pods on AKS when the cluster relies on Standard Load Balancer outbound rules instead of an explicit NAT Gateway.
- **Single-cluster-loss absorption per [ADR-0010](0010-active-active-multi-cluster-topology.md).** Each cluster is sized to ~60–65 % of the total target so survivors absorb the rest under the documented [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) degradation. The SKU has to be chosen against the *absorbed* number, not the steady-state number.
- **Per-tier admission caps in the historical implementation.** `AgentTierOptions` set `RealtimeVoice = 50_000`, `ChatCompletionTts = 150_000`, `SmallLanguageModel = 200_000`. The 350k aspirational total exceeds the RealtimeVoice cap by design — graceful degradation, not extra VMs, is the answer for the head-room past 50k realtime calls per cluster.

## Decision

### Per-pod resource shape

`voice-edge` pods request **4 vCPU / 8 GiB** and are limited at **6 vCPU / 12 GiB**, with a soft target of **1,000 concurrent streaming-mode calls per pod**. The shape is justified by:

- 1,000 calls × 2 mCPU avg = 2 vCPU sustained; the extra 2 vCPU request covers GC, OTel export, the ASP.NET request pipeline for callbacks, and gives the kernel scheduler enough slack to keep WS read latency under one frame interval.
- 1,000 calls × 3.5 MB working set ≈ 3.5 GiB + ~1 GiB runtime + .NET native heap + GC slack ≈ 5–7 GiB sustained; the 8 GiB request and 12 GiB limit are sized so a Gen2 collection does not trip the cgroup OOM killer under burst.
- CPU **limit** is intentionally set above the request (1.5×) — voice traffic is bursty and CPU throttling under a tight limit will manifest as audible jitter on agent speech. Memory limit stays tight because OOM is the worse failure mode.
- The 1k-call ceiling is not from any single bottleneck — CPU, memory, and bandwidth all have headroom — but rather from blast radius and Kestrel WebSocket P99 latency, which start to fan out above ~2k sockets/pod (2 WS × 1k calls). Do not raise the ceiling without re-validating Gen2 GC P99 and WS read latency at the new density.

### CPU node-pool VM SKU

Two SKUs are sanctioned for the `cpu` node pool. The default depends on the tier:

| SKU | vCPU / RAM / local SSD / NIC | `voice-edge` pods per node¹ | Calls per node | Use when |
|---|---|---|---|---|
| `Standard_D16ds_v5` | 16 / 64 GiB / 600 GiB / ~12.5 Gbps | **3** | ~3,000 | **Default for 50k and 100k tiers.** Smaller blast radius, finer cluster-autoscaler granularity, lower per-node cost. |
| `Standard_D32ds_v5` | 32 / 128 GiB / 1.2 TiB / ~16 Gbps | **6** | ~6,000 | **Default at the 350k tier (per-cluster slice ≥ ~150k).** Lower node count, lower DaemonSet/sidecar overhead per call, simpler control-plane footprint. |

¹ Pod count per node is the integer floor of `(vCPU − 2 reserved) / 4` for vCPU and `(RAM − 4 GiB reserved) / 8 GiB` for memory, whichever is smaller. The 2 vCPU / 4 GiB system reservation covers `kubelet`, OTel collector / Fluent Bit, CSI drivers, kube-proxy, the workload-identity webhook, and a service-mesh sidecar if one is enabled.

**Mandatory features on the SKU:**

- **Accelerated Networking** (default on these SKUs but must be confirmed via the cluster-creation manifest). The audio path is sustained-rate small-packet traffic; without SR-IOV the per-packet softirq cost is the limit, not the documented NIC bandwidth.
- **Ephemeral OS disk on local SSD.** The `Dds_v5` series is chosen over the diskless `Ds_v5` precisely so OTel/Fluent Bit can spool to local SSD without contending with the OS disk and without burning managed-disk IOPS for log buffering.

**Disallowed SKUs:** `F`-series (vCPU:RAM ratio of 1:2 leaves no GC slack at 1k calls/pod), `E`-series (over-spec'd on memory, wasted spend), burstable `B`/`Av2` (CPU credits collapse under burst), and `Dpsv5`/`Dpdsv5` Arm SKUs (ACS streaming SDK and the realtime provider SDK are not currently validated on Arm — revisit when ARM64 builds are first-class).

### Per-tier deployment shape

The numbers below assume the **default SKU for the tier** and the [ADR-0010](0010-active-active-multi-cluster-topology.md) sizing rule (each cluster carries 60–65 % of the total so a single-cluster loss is absorbed by survivors). Pod counts are HPA *maximum* — the steady-state minimum is the absorbed-load number, the maximum adds ~30 % headroom for burst arrivals and rolling upgrades.

| Tier | Topology | Calls per cluster (steady / absorb) | Pods per cluster (min → max) | Nodes per cluster (steady → max) | NAT GW public IPs per cluster |
|---|---|---|---|---|---|
| **50k** | 2 clusters × D16ds_v5 | 30k / 50k | 30 → 45 | 12 → 15 | 4 |
| **100k** | 2 clusters × D16ds_v5 | 60k / 100k | 60 → 85 | 22 → 28 | 6 |
| **350k (2-cluster)** | 2 clusters × D32ds_v5 | 210k / 350k | 210 → 280 | 30 → 40 | 12 |
| **350k (3-cluster)** | 3 clusters × D32ds_v5 | 135k / 200k | 140 → 190 | 20 → 30 | 8 |

The 3-cluster option for the 350k tier is preferred when (a) the per-cluster realtime AI quota cannot be raised to absorb 50 % of 350k, or (b) operations wants smaller per-cluster blast radius. [ADR-0010](0010-active-active-multi-cluster-topology.md) explicitly supports N≥2.

### HPA and KEDA

`voice-edge` scales on a **two-signal** policy. Both signals must be in place; neither alone is sufficient.

- **HPA on `pod_websockets_active`** (the custom metric exposed by the `Calling.Edge` meter via the .NET OTel SDK and scraped into the AKS managed-Prometheus / Azure Monitor metric pipeline). Target **800 streaming-mode WS per pod** (80 % of the 1,000 ceiling). `scaleUp` stabilization 30 s with +50 % per minute step; `scaleDown` stabilization **10 min** with −10 % per minute step. Long scale-down is deliberate — call duration is on the order of minutes and aggressive scale-down causes pod churn that breaks streaming-mode calls.
- **KEDA scaler on the Redis `cap:tier:RealtimeVoice` counter** ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) namespace). This is the cross-cluster admission signal — when the **other** cluster is approaching its ceiling, the local cluster pre-warms before its own HPA signal catches up. Without this, a cluster failover sees a 60–90 s pod-warm-up gap during which the survivor over-admits and breaches the [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) tier ceiling.

**PodDisruptionBudget:** `minAvailable = 90 %`. Streaming-mode WS calls re-establish through ACS reconnect on pod loss; verb-mode calls survive directly. The 90 % budget bounds the number of in-flight streaming calls that re-bind during a node drain.

**Pod anti-affinity:** soft (`preferredDuringScheduling…`) across `topology.kubernetes.io/zone` so a single-AZ event takes at most 1/3 of the pods, but does not block scheduling when one zone is short.

### SNAT — Azure NAT Gateway, not Standard Load Balancer outbound

The CPU node pool's subnet **must** be attached to an Azure NAT Gateway. AKS default outbound (Standard LB outbound rules) does not scale to the per-cluster upstream WS count at the 100k or 350k tiers. With one persistent upstream WS per call to a small set of provider front-door IPs, the 5-tuple `(podIP, srcPort, providerIP, 443, TCP)` collapses to roughly `srcPort` uniqueness per source-IP / destination-IP pair — a single front-end IP runs out of ports at ~64k connections.

- NAT Gateway public IPs per cluster come from the table above. The math is `ceil(2 × peak_upstream_WS / 64,000)` — the 2× accounts for TIME_WAIT, churn during re-bind storms, and the secondary outbound chatter (Azure Speech for verb-mode calls, Cosmos, Redis, App Configuration, Key Vault, OTel export).
- `idleTimeoutInMinutes: 30` on the NAT Gateway. Matches typical realtime provider WS keepalive intervals and prevents premature idle-close of mid-call upstream sessions.
- Operators monitor **`SNATPortAllocationFailed`** and **`SNATConnectionCount`** on the NAT Gateway in Azure Monitor; either non-zero on the former or the latter approaching 80 % of provisioned ports is a sizing breach and pages.

### Cluster identity

Node pools across clusters are otherwise identical. The only per-cluster differentiator visible to the historical workload is the `HYPERSCALE_CLUSTER_ID` environment variable (`ClusterIdentityOptions.ClusterIdEnvironmentVariable` in `HyperscaleOptions.cs`), set in the deployment manifest per the [ADR-0010](0010-active-active-multi-cluster-topology.md) cluster-identity contract.

## Consequences

- **Two sanctioned SKUs is intentional.** A single SKU across the full range would either over-pay at 50k (D32ds_v5 wasting half a node) or under-pack at 350k (D16ds_v5 tripling the node count and proportionally the DaemonSet overhead). Two SKUs covers the range with one well-understood crossover point at the 350k tier.
- **The 1k-call/pod ceiling is the single biggest knob and must be validated, not assumed.** Each new realtime AI provider, each new ACS framing change, and each .NET major upgrade can move the GC and WS-latency curves. A standing load test at 1k calls/pod against a D16ds_v5 pod, run before each release, is a release-gate requirement.
- **Voice-edge cost is dominated by the CPU pool node count.** At 350k aspirational, this is ~30 D32ds_v5 nodes per cluster × 2 clusters = ~60 nodes minimum, ~80 with HPA headroom. The cost-leverage move at that scale is *not* a smaller SKU; it is reducing the per-call working set (smaller bounded channels if measurements support it, ArrayPool tuning, fewer scoped services in the per-call DI graph).
- **NAT Gateway is the single most common pre-prod-to-prod surprise.** A cluster that ran fine on a 5k-call load test against LB outbound will SNAT-exhaust at 30–40k calls in production. The pre-prod test plan must include a NAT Gateway with the *same* number of public IPs as the target tier, not the default.
- **The 3-cluster posture at 350k carries a real per-cluster overhead cost.** Each additional cluster adds an ACS resource, an Event Grid system topic, a Cognitive Services link, a per-cluster Azure Monitor workspace, a synthetic prober, and a separate Helm release. The default at 350k stays at **2 clusters × D32ds_v5**; the 3-cluster option is opt-in for the cases listed above.
- **Burst arrival is the worst HPA case, not steady-state growth.** A blast of 10k callers in 30 s (DR drill, public-emergency event) cannot be served by HPA alone — the new pods take ~60 s to warm. Mitigation is the KEDA pre-warm signal above and an over-provision factor in the *minReplicas* (the "steady" column in the tier table includes the absorb factor, not just the steady state).
- **System-component overhead per node is fixed, not proportional.** kube-proxy, OTel collector, Fluent Bit, CNI, workload-identity webhook, and (if used) a service-mesh sidecar add ~1.5–2 vCPU and ~3 GiB per node *regardless of pod density*. The pod-count math above already reserves 2 vCPU / 4 GiB; doubling pods per node by going to D32ds_v5 effectively halves this per-call overhead.
- **No Spot / preemptible nodes.** Streaming-mode calls cannot tolerate pod eviction without an audible re-route or drop. The CPU pool is regular priority only. If a cost-shaping pool is added later it must be a *separate* node pool used only for stateless background workloads, not `voice-edge`.

## Alternatives considered

- **One SKU across all tiers (D16ds_v5 everywhere).** Rejected at 350k for the reasons above — node count and per-call DaemonSet overhead become measurable cost lines.
- **One SKU across all tiers (D32ds_v5 everywhere).** Rejected at 50k and 100k — wastes capacity at low end and the cluster autoscaler granularity (one extra node = 6,000 calls of headroom) is too coarse for the smaller tiers' burst-absorb behavior.
- **F32s_v2 or other compute-optimized SKU.** Rejected. Memory-per-vCPU is too tight for the per-call working set + GC headroom. At 1k calls/pod we measured Gen2 collections > 1/min on F-series under load.
- **Dlds_v5 / Dlsv5 (memory-light).** Rejected for the same reason — half the memory per vCPU of D-series and no room for the per-call working set.
- **Standard Load Balancer outbound instead of NAT Gateway.** Rejected. Workable up to ~30k calls/cluster on aggressive port allocation; not at 100k+, and the failure mode (silent SNAT exhaustion under load that test cells can't reproduce) is operationally unacceptable.
- **Single larger SKU per node (D64*) to maximize density.** Considered and rejected for now — limited Azure region availability for the very large D-series SKUs, larger blast radius (a single-node loss takes ~12k calls), and packing efficiency past 6 pods/node is not better than two D32ds_v5 nodes. Re-evaluate if a >350k tier becomes a hard requirement.
- **Mixed CPU pool (one SKU for streaming-mode, one for verb-mode).** Rejected. Same `voice-edge` Deployment serves both; splitting into two Deployments to land on two SKUs would require a routing-layer change for which there is no offsetting benefit at the documented per-pod ceiling.
- **Self-hosted realtime model on the same node as `voice-edge`.** Out of scope — collapses into the upcoming model-selection ADR. The CPU pool defined here assumes the realtime path stays Azure-hosted, consistent with [`aks-topology.md`](../architecture/aks-topology.md).

## Related

- [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) — Redis namespaces consumed by the KEDA pre-warm scaler.
- [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) — Bi-di WS pinning that drives the per-pod blast-radius argument.
- [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) — Tier ceilings the per-cluster admission honors.
- [ADR-0010](0010-active-active-multi-cluster-topology.md) — Multi-cluster sizing rule (60–65 % per cluster) used in the per-tier table.
- [ADR-0011](0011-pod-ownership-and-lease-model.md) — Pod-loss UX assumed by the PDB and HPA scale-down policy.
- [`docs/architecture/aks-topology.md`](../architecture/aks-topology.md) — Deployment view this ADR fills in the SKU details for.
- **(forthcoming)** ADR-0013 — Model selection for intent / NLU / Azure STT-TTS / realtime backends. Owns the GPU node-pool SKU and the per-tier model concurrency caps that interact with this ADR's `voice-edge` density.
