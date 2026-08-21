# ADR-0015 — Dedicated AKS Istio ingress gateway node pool

- **Status:** Accepted
- **Date:** 2026-07-17

## Context

The AKS Istio service mesh add-on runs its ingress gateways in-cluster. The gateway is therefore part of the realtime voice data plane: it terminates TLS, maintains long-lived WebSocket connections, forwards small audio frames, and emits proxy telemetry before traffic reaches `voice-edge`. Sharing its nodes with application or GPU workloads would couple ingress availability to workload CPU bursts, node drains, and autoscaler decisions.

The add-on supports preferential placement on nodes labeled `azureservicemesh/istio.replica.preferred: true`. For add-on revision `asm-1-30` and later, this label has affinity weight 100 and AKS system nodes have weight 50. This is a preference, not a hard scheduling constraint: if preferred nodes are absent or unschedulable, gateway pods can fall back to system nodes. A minor Istio revision upgrade also creates a second gateway deployment for the new revision, temporarily increasing the required capacity.

[ADR-0012](0012-aks-node-pool-vm-sku-and-pod-density.md) selects the VMs and pod density for `voice-edge`; it does not select the independently scaled ingress tier. The ingress selection is driven primarily by:

- per-core TLS and Envoy filter performance;
- connection-establishment rate and long-lived connection count;
- small-packet network processing and network bandwidth;
- enough memory for connection state, telemetry buffers, and overlapping gateway revisions;
- local storage for an Ephemeral OS disk and node-level log buffering; and
- multi-zone capacity in the deployment region.

As of 2026-07-17, the newest AMD Turin `D8ads_v7` and `D8alds_v7` sizes have attractive CPU and 25 Gbps networking, but the East US 2 subscription SKU inventory exposes them only in availability zone 1. `Standard_D8ds_v6` is available in zones 1, 2, and 3. It provides 8 vCPU, 32 GiB RAM, Intel Emerald Rapids, Accelerated Networking with MANA, 12.5 Gbps maximum advertised aggregate bandwidth, local NVMe, and Ephemeral OS disk support.

## Decision

Run the managed Istio ingress gateways on a dedicated Linux user node pool named `istiogw` with the following baseline:

| Setting | Decision |
|---|---|
| VM SKU | `Standard_D8ds_v6` |
| Initial/minimum nodes | 3 |
| Availability zones | 1, 2, and 3 |
| Autoscaling | Enabled; minimum 3, maximum set from tested peak capacity (initially 12) |
| OS | Azure Linux |
| OS disk | 64 GiB Ephemeral OS disk |
| Placement label | `azureservicemesh/istio.replica.preferred=true` |
| Priority | Regular only; Spot is prohibited |
| Custom taint | None |

The initial Azure CLI shape is:

```powershell
az aks nodepool add `
  --resource-group $RESOURCE_GROUP `
  --cluster-name $CLUSTER_NAME `
  --name istiogw `
  --mode User `
  --node-vm-size Standard_D8ds_v6 `
  --zones 1 2 3 `
  --node-count 3 `
  --enable-cluster-autoscaler `
  --min-count 3 `
  --max-count 12 `
  --os-sku AzureLinux `
  --node-osdisk-type Ephemeral `
  --node-osdisk-size 64 `
  --labels azureservicemesh/istio.replica.preferred=true
```

The maximum of 12 is a starting guardrail, not a throughput claim. Set the production maximum and quota from a gateway load test that reproduces TLS handshakes, connection churn, long-lived WebSockets, audio frame sizes, telemetry, and an overlapping Istio revision upgrade.

### Availability and scheduling

- Keep at least one node in each availability zone and enough total spare capacity to lose one zone while maintaining the required gateway replicas.
- Do not put a custom `NoSchedule` taint on the pool unless the currently installed managed gateway revision is verified to tolerate it. The supported placement mechanism is the preferred-node label.
- Keep the system pool capable of hosting the gateway's minimum viable replica count. System nodes are the add-on's scheduling fallback when preferred nodes cannot accept pods.
- Reserve at least 20% node capacity for kubelet, CNI, monitoring, log collection, and upgrade overlap. Before a minor Istio revision upgrade, confirm the pool can host both old and new gateway deployments simultaneously.
- Use a PodDisruptionBudget and topology spread behavior exposed by the managed add-on; verify effective resources after each add-on revision rather than patching managed deployments with configuration that the add-on can overwrite.

### Traffic policy

Set the external gateway service to `externalTrafficPolicy: Local` when preserving the client source IP and avoiding the second cross-node hop are required:

```powershell
kubectl patch service aks-istio-ingressgateway-external `
  -n aks-istio-ingress `
  --type merge `
  --patch '{"spec":{"externalTrafficPolicy":"Local"}}'
```

With `Local`, load-balancer health and traffic distribution depend on a healthy local gateway endpoint. Keep gateway replicas distributed across zones and validate behavior during a zone failure. Configure the Azure Load Balancer TCP idle timeout above the longest expected quiet period, with application-level WebSocket keepalives remaining the primary liveness mechanism.

### Capacity and observability

Do not size the pool from average CPU alone. Scale and alert from the combination of:

- Envoy downstream active connections and connection-open rate;
- TLS handshake rate, failures, and handshake latency;
- node CPU, throttling, memory working set, and network packets/bytes;
- gateway request and WebSocket upgrade errors;
- downstream connection duration and abnormal disconnects;
- Azure Load Balancer health-probe status; and
- pending or unschedulable gateway pods during upgrades and zone disruptions.

Treat sustained node CPU or network utilization above 60–70% as a signal to add headroom and investigate before saturation. The exact threshold and connections-per-pod target must come from load tests because Envoy filters, TLS policy, access logging, tracing sample rate, and connection churn materially change gateway capacity.

### SKU review policy

Re-evaluate the SKU before each production-region rollout and at least annually. Promote `Standard_D8ads_v7` to the default when all of these are true in the target subscription and region:

1. It is available without restriction in every availability zone used by the pool.
2. AKS and the selected Azure Linux node image support it.
3. Regional quota and replacement capacity are available.
4. A representative load test shows equal or better tail latency, TLS throughput, and connection stability.
5. Its total cost per supported connection is lower after accounting for node and gateway replica counts.

`D8alds_v7` is a cost-oriented alternative only if a load test proves that 16 GiB per node retains sufficient memory headroom during telemetry bursts and dual-revision upgrades. Preview network-optimized sizes such as `Dndsv6` are not production defaults.

## Consequences

- Gateway failures and scaling events are isolated from `voice-edge`, GPU inference, and system-node workload pressure.
- Three-zone placement avoids making a newer but single-zone SKU an ingress-tier availability dependency.
- `D8ds_v6` costs more and advertises less bandwidth than the newest v7 candidates, but it provides the required zone coverage and twice the memory of `D8alds_v7` in the current region.
- Three minimum nodes create a fixed baseline cost. This is accepted because two nodes cannot preserve one gateway-node failure per zone and still provide three-zone placement.
- Soft affinity deliberately preserves a scheduling escape path. It also means capacity exhaustion can move gateway load onto system nodes, so that fallback must be monitored and included in failure testing.
- `externalTrafficPolicy: Local` removes an avoidable cross-node hop and preserves source IP, but makes topology distribution and local health endpoints operationally important.
- SKU availability is regional and subscription-sensitive. The decision records a dated East US 2 observation, not a claim that `D8ds_v6` is universally preferable.

## Alternatives considered

- **`Standard_D8ads_v7`.** Preferred future replacement: 8 vCPU, 32 GiB, AMD Turin, MANA, 25 Gbps, and Ephemeral OS disk support. Rejected for the current East US 2 rollout because it is available only in zone 1.
- **`Standard_D8alds_v7`.** Approximately lower-cost with the same 25 Gbps advertised network limit and local NVMe. Rejected as the production default because it is currently single-zone and its 16 GiB memory gives less upgrade and telemetry headroom.
- **`Standard_F8als_v7`.** Full physical cores and 25 Gbps are attractive for TLS-heavy traffic. Rejected because it is currently single-zone, has 16 GiB RAM, and does not support Ephemeral OS disks.
- **`Standard_D8ds_v5`.** Available across all three East US 2 zones and slightly less expensive. Retained as a capacity fallback, but v6 is the default for its newer CPU, memory bandwidth, NVMe, and MANA platform.
- **Network-optimized `Dndsv6`.** Its enhanced connection setup and bandwidth fit an ingress workload. Rejected while the series remains in Preview; reconsider after general availability and regional validation.
- **Run gateways on the system pool.** Supported by the add-on but rejected as the steady-state architecture because ingress traffic could starve critical cluster services and system activity could disrupt the voice data plane.
- **Run gateways on the `voice-edge` pool.** Rejected because gateway and application scaling, upgrades, and resource pressure should have independent failure domains.
- **Hard isolation with a custom taint.** Rejected because the managed add-on documents preferred affinity and system-node fallback, not a stable user-managed toleration contract for arbitrary taints.

## Validation

Before production, capture the dated result of these checks in the deployment evidence:

```powershell
az vm list-skus `
  --location eastus2 `
  --resource-type virtualMachines `
  --all

kubectl get pods -n aks-istio-ingress -o wide
kubectl get deployment,hpa,pdb -n aks-istio-ingress
kubectl get nodes -L azureservicemesh/istio.replica.preferred,topology.kubernetes.io/zone
```

Run normal peak, burst connection, one-node drain, one-zone loss, cluster-autoscaler expansion, and minor Istio revision upgrade scenarios. Acceptance requires no material increase in failed upgrades, abnormal WebSocket disconnects, or gateway P99 latency.

## Related

- [ADR-0010](0010-active-active-multi-cluster-topology.md) — cluster-level availability and failure absorption.
- [ADR-0012](0012-aks-node-pool-vm-sku-and-pod-density.md) — `voice-edge` VM selection and per-pod density.
- [`aks-topology.md`](../architecture/aks-topology.md) — deployment view and node-pool layout.
- [AKS Istio ingress gateway documentation](https://learn.microsoft.com/azure/aks/istio-deploy-ingress) — managed gateway deployment, placement preference, service customization, and traffic policy.
- [Ddsv6 size documentation](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/ddsv6-series) — selected SKU specifications.
- [Daldsv7 size documentation](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/daldsv7-series) — current cost-oriented future candidate.
- [Dadsv7 size documentation](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/dadsv7-series) — current 32 GiB future candidate.
