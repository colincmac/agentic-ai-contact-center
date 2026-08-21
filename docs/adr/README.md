# Architecture Decision Records

Architecture Decision Records (ADRs) capture consequential choices, the
alternatives considered, and their consequences. The ADR file is authoritative
for status. Accepted records are historical; a changed decision requires a new
ADR that supersedes the old one.

## Index

| ID | Decision | Status |
| --- | --- | --- |
| [0000](0000-azure-configuration-keyvault-strategy.md) | Azure App Configuration and Key Vault strategy | Draft |
| [0001](0001-pstn-ingress-via-tpe.md) | PSTN ingress via Teams Phone Extensibility | Accepted |
| [0002](0002-acs-call-automation-as-control-plane.md) | ACS Call Automation as the call control plane | Accepted |
| [0003](0003-incomingcall-delivery-via-event-grid.md) | `IncomingCall` delivery via Event Grid | Accepted |
| [0004](0004-call-state-in-redis-by-callconnectionid.md) | Redis coordination keyed by `callConnectionId` | Accepted |
| [0005](0005-escalation-blind-vs-consultative-transfer.md) | Blind versus consultative escalation transfer | Accepted |
| [0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) | Azure VoiceLive versus OpenAI `gpt-realtime` | Proposed |
| [0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) | DTMF over media WebSocket versus callback API | Accepted |
| [0008](0008-graceful-degradation-realtime-to-dtmf.md) | Graceful degradation from realtime AI to DTMF | Accepted |
| [0009](0009-voice-biometrics-stub-vs-grpc.md) | Pluggable voice biometrics evaluator | Accepted |
| [0010](0010-active-active-multi-cluster-topology.md) | Active-active multi-cluster topology | Accepted |
| [0011](0011-pod-ownership-and-lease-model.md) | Pod ownership and lease model | Accepted |
| [0012](0012-aks-node-pool-vm-sku-and-pod-density.md) | AKS node-pool SKU and pod density | Accepted |
| [0013](0013-sandboxed-tool-execution-via-aca-sandboxes.md) | Sandboxed tool execution via ACA Sandboxes | Proposed |
| [0014](0014-call-state-event-folded-provider-slices.md) | Event-folded call-state provider slices | Accepted |
| [0015](0015-aks-istio-ingress-gateway-node-pool.md) | Dedicated AKS Istio ingress gateway node pool | Accepted |

## Authoring

Use [`templates/adr.template.md`](templates/adr.template.md) for a new record.
Use the next sequential four-digit identifier, keep all required sections, and
link evidence when it exists. If evidence is absent, state that explicitly.
