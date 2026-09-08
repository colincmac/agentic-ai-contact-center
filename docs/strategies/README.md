# Conversation strategies

The design assigns each active call exactly one `IConversationStrategy` instance — the "brain" of the call. The strategy consumes caller input (audio frames, DTMF tones), emits outbound directives (audio, verbs, transfers), and publishes structured `StrategyEvent`s for observers and dashboards.

This index maps the current library to the strategy design imported from the
source showcase. The library is present under
[`code/Agents.AI.ContactCenter/Calling/`](../../code/Agents.AI.ContactCenter/Calling/);
the imported design is not a guarantee that every strategy or handoff is
implemented.

| Document | Strategies covered |
| --- | --- |
| [`conversation-strategies.md`](conversation-strategies.md) | Historical strategy contract and handoff model; not a current copy-and-run API guide |

## Current implementation

| Strategy | Current source | Implemented interaction |
| --- | --- | --- |
| Realtime | [`RealtimeCallWorkflowStrategy`](../../code/Agents.AI.ContactCenter/Calling/Strategies/RealtimeVoice/RealtimeCallWorkflowStrategy.cs) | Realtime agent audio, stage prompts/tools, and DTMF menu shortcuts |
| Intent NLU | [`NluCallWorkflowStrategy`](../../code/Agents.AI.ContactCenter/Calling/Strategies/Nlu/NluCallWorkflowStrategy.cs) | Streaming speech recognition, intent classification, synthesized prompts, and DTMF shortcuts |
| DTMF | [`DtmfCallWorkflowStrategy`](../../code/Agents.AI.ContactCenter/Calling/Strategies/Dtmf/DtmfCallWorkflowStrategy.cs) | DTMF navigation/credential collection and synthesized PCM prompts |
| Composite | [`CompositeFallbackStrategy`](../../code/Agents.AI.ContactCenter/Calling/Strategies/Composite/CompositeFallbackStrategy.cs) | Ordered leaf-strategy selection and reactive fault fallback |

The [registration surface](../../code/Agents.AI.ContactCenter/DependencyInjection/CallWorkflowStrategyExtensions.cs)
supplies the three leaf strategies above. `ChatCompletionTts` and
`SmallLanguageModel` in [AgentTierOptions](../../code/Agents.AI.ContactCenter/Configuration/AgentTierOptions.cs)
are not additional implemented strategies. The code's enum numbering is not
the four-tier architectural numbering in
[ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md).

Current DTMF depends on a synthesizer and an audio-capable edge. The
[ACS verb edge](../../code/Agents.AI.ContactCenter/Calling/Core/AcsCallAutomationEdge.cs)
is a separate primitive, not a wired verb-mode DTMF strategy or the
speech-independent prerecorded floor required by
[ADR-0007](../adr/0007-dtmf-bidirectional-websocket-vs-callback-api.md) and ADR-0008.
Workflow/action parity across tiers, admission on every startup-fallback path,
and caller-authentication behavior remain adoption gaps.

Current per-call state uses
[`CallStateProjector`](../../code/Agents.AI.ContactCenter/State/CallStateProjector.cs)
with `AuthSnapshot` and `IvrSnapshot`. State restoration does not restore a
lost media socket: [ADR-0011](../adr/0011-pod-ownership-and-lease-model.md)
requires an audible reroute for an orphaned streaming call. See
[current implementation status](../README.md#current-implementation) and
[functional-test limitations](../evidence/2026-09-08-code-adoption-validation.md#limitations)
before treating the design as implemented or validated.

## Review direction: interaction profiles

This is a design recommendation, not a new schema accepted by the current YAML
reader or a revision of the accepted ADR tier model.

Keep business flow, interaction policy, caller-authentication policy, and ingress
configuration separate. Prefer named, configurable interaction profiles over
assigning permanent business meaning to numeric tier values:

| Profile example | Interaction | Dependency boundary |
| --- | --- | --- |
| Realtime voice | Native realtime speech with deterministic action authorization | Realtime provider and live media session |
| Scripted speech / intent NLU | STT, constrained intent interpretation, and scripted TTS | Speech services plus the configured classifier, which may itself be a model |
| Recorded DTMF | ACS callback-based digit collection and recorded prompts | ACS call control and reachable prompt assets; no live model or speech synthesis |
| Overflow | Recorded announcement and configured transfer or clean termination | Explicit emergency route; not a claim of unlimited downstream capacity |

A TTS-backed DTMF profile is also valid, but it is not independent of a Speech
outage. An SLM can supply a classifier or conversational text backend; it does
not require a different business-flow contract.

Expected per-stage modality changes are not the same as failure-driven
degradation. For example, a realtime conversation can use isolated deterministic
DTMF capture for a PIN without making the model responsible for collecting the
secret. Human handoff is an explicit flow action, not merely a lower AI tier.

Enable only profiles with configured dependencies. Select among them using the
active stage's capabilities, provider health, and available capacity. Every
activation, including startup failure and mid-call fallback, needs admission.
Preserve the workflow revision, validated data, completed-action identities, and
still-valid authentication evidence. A profile change must never weaken an
action's authorization policy. If no eligible profile can complete the stage,
use its explicit failure/overflow route rather than silently changing the task.

Workflow execution and container hosting are different choices. Orleans is a
candidate for coordinating short per-call control events, not a reason to route
PCM frames through durable workflow state. Microsoft documents Orleans hosting
on both [Kubernetes](https://learn.microsoft.com/dotnet/orleans/deployment/kubernetes)
and [Azure Container Apps](https://learn.microsoft.com/dotnet/orleans/deployment/deploy-to-azure-container-apps).
Those hosting examples are not evidence of this workload's capacity. Keep
hosting-specific services outside business-flow definitions, and retain the
media-owner/reroute constraint in ADR-0011.

## Companion ADRs

- [ADR-0008 — Graceful degradation: Realtime → DTMF](../adr/0008-graceful-degradation-realtime-to-dtmf.md)
- [`architecture/call-flow.md`](../architecture/call-flow.md) — where strategies sit relative to ACS Call Automation and the call edge
