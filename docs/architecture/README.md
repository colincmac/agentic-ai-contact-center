# Architecture

Index over the architecture views that already exist in this repository. Nothing here is a new source of truth — every row links to the canonical document, which stays where it is.

## Who owns which question

| Question | Owner | Where |
| --- | --- | --- |
| *Why* did we choose this? What were the alternatives and consequences? | Architecture decision records | [`../adr/`](../adr/) |
| *What* does the system look like, and how do the parts interact? | Architecture views (this folder and its neighbours) | this page |
| *How* do I run, provision, or recover it? | Runbooks and provisioning guides | [`../runbooks/README.md`](../runbooks/README.md) |
| *How* do I see what happened on a call? | Monitoring and correlation model | [`../monitoring/README.md`](../monitoring/README.md) |
| *What have we actually proven* versus modeled? | Evidence catalog | [`../evidence/README.md`](../evidence/README.md) |

An architecture view never overrides an ADR. If a view and an ADR disagree, the ADR wins and the view is stale.

## By reader intent

### "Walk me through a call"

| Document | What it gives you |
| --- | --- |
| [`call-flow.md`](call-flow.md) | End-to-end narrative call flow: PSTN → Teams resource account → ACS Call Automation → IVR → Dynamics CCaaS. The canonical flow document referenced by ADR-0001 through ADR-0005. |
| [`sequence-diagrams.md`](sequence-diagrams.md) | The same flow as a mermaid sequence diagram, including the happy-path escalation. |
| [`transfer-patterns.md`](transfer-patterns.md) | Blind versus consultative transfer, and the VoIP-vs-SIP transport rules for `customCallingContext` headers. |

### "Show me how the conversation is driven"

| Document | What it gives you |
| --- | --- |
| [`../strategies/README.md`](../strategies/README.md) | Conversation-strategy catalog: Realtime, NLU, DTMF, Composite — what each emits and how tier changes are handled. |
| [`../strategies/conversation-strategies.md`](../strategies/conversation-strategies.md) | The detailed strategy contract and the events each strategy raises. |

The [implementation map](../README.md#current-implementation) identifies the
libraries now present under `code/` and the gaps between them and the imported
strategy design. Aspire cloud/resource composition is intentionally deferred
pending SKU, region, and capacity recommendations; application endpoint
composition is a separate integration deliverable. The
[local functional validation](../evidence/2026-09-08-code-adoption-validation.md)
does not establish production behavior or capacity.

### "Show me how it is deployed and how it scales"

| Document | What it gives you |
| --- | --- |
| [`aks-topology.md`](aks-topology.md) | Deployment view: application deployments plus managed Istio ingress across AKS workload pools, hybrid sticky-WebSocket + stateless-webhook routing, the ACA-today / AKS-tomorrow mapping, and the showcase → pilot → hyperscale scaling matrix. Consumes ADR-0004 / 0008 / 0010 / 0011 / 0015. |

The scaling matrix in `aks-topology.md` is a **modeled** design target, not a measured result. See [`../evidence/README.md`](../evidence/README.md) before quoting any number from it.

### "Show me how it is observed"

| Document | What it gives you |
| --- | --- |
| [`../monitoring/README.md`](../monitoring/README.md) | The observability plane, its one honest constraint (canonical-ID join, not a single distributed trace), and the index of monitoring documents. |
| [`../monitoring/correlation-model.md`](../monitoring/correlation-model.md) | The `e2e_call_id` / `context_id` contract and the OpenTelemetry attribute keys. |

## Decisions that shape these views

Statuses live in the ADR files and are authoritative there; this table is a pointer, not a copy.

| Area | Decisions |
| --- | --- |
| Ingress and call control | [ADR-0001](../adr/0001-pstn-ingress-via-tpe.md), [ADR-0002](../adr/0002-acs-call-automation-as-control-plane.md), [ADR-0003](../adr/0003-incomingcall-delivery-via-event-grid.md) |
| Call state and coordination | [ADR-0004](../adr/0004-call-state-in-redis-by-callconnectionid.md), [ADR-0014](../adr/0014-call-state-event-folded-provider-slices.md), [ADR-0011](../adr/0011-pod-ownership-and-lease-model.md) |
| Conversation tiers and AI providers | [ADR-0006](../adr/0006-realtime-ai-voicelive-vs-gpt-realtime.md), [ADR-0007](../adr/0007-dtmf-bidirectional-websocket-vs-callback-api.md), [ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md) |
| Escalation | [ADR-0005](../adr/0005-escalation-blind-vs-consultative-transfer.md) |
| Identity and biometrics | [ADR-0009](../adr/0009-voice-biometrics-stub-vs-grpc.md), [ADR-0000](../adr/0000-azure-configuration-keyvault-strategy.md) |
| Cluster topology and capacity | [ADR-0010](../adr/0010-active-active-multi-cluster-topology.md), [ADR-0012](../adr/0012-aks-node-pool-vm-sku-and-pod-density.md), [ADR-0015](../adr/0015-aks-istio-ingress-gateway-node-pool.md) |
| Tool execution | [ADR-0013](../adr/0013-sandboxed-tool-execution-via-aca-sandboxes.md) |

## Machine-readable index

[`../solution-manifest.yaml`](../solution-manifest.yaml) lists the same entry points for tooling, along with the paths that are excluded from synthesis.
