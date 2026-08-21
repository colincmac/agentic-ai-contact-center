# ADR-0013 — Sandboxed tool execution via Azure Container Apps Sandboxes (ADC microVMs)

- **Status:** Proposed
- **Date:** 2026-06-17

## Context

The realtime contact-center agent runs LLM-driven IVR workflows. Today every tool the
model can call executes **in-process** inside the AKS voice pod: `CallControlTools`,
`BalanceLookupTools`, `WorkflowStateTools`, `CallerAuthenticationTools`, and the
synthesized per-stage `advance` function. That is the right home for those tools — they
read and emit per-call state events folded into the `CallStateProjector` (e.g. `IvrSnapshot`,
`AuthSnapshot`) and need in-process latency on the realtime hot path.

As the platform grows we want the agent to run a new class of **untrusted or dynamic**
tools:

- a **code interpreter** (model-generated Python for calculations, parsing, transforms),
- third-party / partner **MCP servers** brought along by a tenant,
- **document/attachment parsing** on caller-uploaded files.

Running model-generated code or third-party servers in the AKS pod shares the host kernel
with the media path, the caller's PII, and our workload identity — one kernel CVE or one
successful prompt-injection away from lateral movement. A namespace-isolated container is
the wrong primitive for code we trust less than our own deployment.

[Azure Container Apps Sandboxes (preview)](https://learn.microsoft.com/en-us/azure/container-apps/sandboxes-overview)
— ARM type `Microsoft.App/sandboxGroups` — provides per-sandbox **microVM** isolation with a separate
guest kernel, sub-second boot, snapshot/scale-to-zero, an egress proxy, and native **Entra
Agent Identity** via `IDENTITY_ENDPOINT`. This will host the AI Agents' **tool-execution
surface** without moving the call itself.

## Decision

1. **Keep the call in AKS; sandbox only tool execution.** `RealtimeCallWorkflowStrategy`,
   the audio/DTMF pumps, the bounded channels, `AuthorizingAIAgent`, the
   `WorkflowExecutor`, and every state-mutating tool stay in-process. ACA Sandbox's networking is
   API/WebSocket-via-control-plane, not raw inbound media — it is the wrong place for
   sub-50ms bidirectional audio.

2. **One sandbox per call, not per tool call.** Provision lazily on first sandboxed-tool
   invocation; reuse across all tool calls on that call; tear down on call-scope dispose.
   Per-tool-call provisioning would let `PUT /sandboxes` dominate latency; per-call
   amortizes it and keeps a warm working directory.

3. **A new scoped lifecycle owner, `ICallSandboxManager`.** Registered in the per-call DI
   scope created by `CallSessionFactory`, so it survives composite-tier swaps
   (RealtimeVoice → IntentNlu → DtmfOnly) for free (same scoped instance every inner
   strategy resolves) and is disposed deterministically with the call. The data-plane
   contract is hidden behind `ICallSandbox` so the **preview** API churn is isolated.

4. **Tools reach the sandbox through the existing scoped-DI seam.** The first consumer —
   a code-interpreter `IAIToolCollection` — resolves `ICallSandboxManager` exactly as
   `CallControlTools` resolves `ICallSessionAccessor`. No change to the tool-resolution
   pipeline (`IIvrToolRegistry` → `CompiledStage.ToolBindings` → `GetToolsFor`) is needed.
   A generic `[SandboxedTool]` attribute + a `DelegatingAIFunction` decorator (composed in
   `AuthorizingAIAgent.ApplyToolMiddleware`) is the planned mechanism for **redirecting**
   arbitrary existing tools (e.g. third-party MCP) into the sandbox; it is a follow-up
   phase, not part of the first slice.

5. **Structured argument marshaling, never shell interpolation.** Tool inputs cross into
   the sandbox as data, not as interpolated shell strings. The code interpreter base64-encodes
   the snippet and decodes it inside the microVM (`base64 -d`), whose alphabet is
   shell-metacharacter-free — eliminating the command-injection footgun in the original
   `SandboxedToolFactory` sketch. Tokens are **never** passed over the exec channel.

6. **Entra Agent Identity per sandbox.** Each sandbox is assigned an agent identity; code
   inside uses the standard `DefaultAzureCredential` / `IDENTITY_ENDPOINT` flow, and the
   Identity Proxy blocks the underlying managed-identity token, so prompt-injected code
   cannot escalate to the AKS workload identity. Deny-by-default egress and a pre-baked disk
   image are part of the same hardening phase.

7. **Configuration-driven and disabled by default.** Mirrors the biometrics
   stub-vs-gRPC pattern ([ADR-0009](0009-voice-biometrics-stub-vs-grpc.md)): `CallSandbox`
   options bind from configuration; when not configured the feature is off and no
   sandboxed tools are registered, so dev/test and existing deployments are unaffected.

### Notes

- **`Suspend`/`Resume` is not snapshot/resume.** `IConversationStrategy.SuspendAsync` is a
  **supervisor barge-in output pause** ("inbound is still delivered so the strategy stays
  caught up") — not idle teardown. Snapshotting there would checkpoint a still-running
  session on every supervisor interjection. Snapshot/scale-to-zero is therefore **excluded
  from v1**; a "returning caller" experience, if pursued, belongs on a call-**start** hook,
  not on Suspend.

## Consequences

- The realtime latency budget tracked by [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md)
  is untouched: the model still talks to the AKS pod for audio; only model-issued tool calls
  take a short async excursion into a microVM.
- A new runtime dependency on the ADC data plane (`management.azuredevcompute.io`, preview)
  appears **only** when the feature is enabled. Preview-to-GA breaking changes are contained
  behind `ICallSandbox`.
- Untrusted code runs with hardware (KVM) isolation, deny-by-default egress, and an identity
  that cannot impersonate the pod — a materially stronger posture than in-process execution.
- Cost is per-call sandbox seconds while tools actually run; scale-to-zero between calls is a
  later optimization, not a v1 commitment.
- New operational surface: sandbox group provisioning, disk-image build/publish per release,
  agent-identity and egress-policy management per tenant.

## Alternatives considered

- **Run untrusted tools in-process (status quo).** Rejected for code interpreter / 3rd-party
  MCP: shared kernel with media + PII + workload identity.
- **A plain Kubernetes pod / sidecar per call.** Namespace isolation, not a VM boundary, and
  no Entra Agent Identity (`idtyp`) semantics; anything with IMDS access can mint MI tokens.
- **Host the whole `RealtimeCallWorkflowStrategy` in a sandbox.** Rejected — adds the entire
  ADC control-plane hop chain into the jittery realtime audio path.
- **One sandbox per tool call.** Rejected — `PUT /sandboxes` latency would dominate; per-call
  granularity amortizes provisioning and keeps warm working state.


## References
- [Azure ACA Sandbox Portal](https://sandboxes.azure.com/)
