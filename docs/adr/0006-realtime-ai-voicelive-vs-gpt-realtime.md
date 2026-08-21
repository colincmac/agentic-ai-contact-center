# ADR-0006 — Realtime AI provider: Azure VoiceLive vs OpenAI `gpt-realtime` (direct)

- **Status:** **Proposed** (decision pending)
- **Date:** WIP — track in the issue tracker before implementing the dependent work

## Context

The agent app needs a **realtime, full-duplex, speech-in / speech-out** AI session for the natural-language portion of a call. The realtime AI session sits behind the `IRealtimeAgent` / `ILiveConversationClient` decorator pipeline already established in the repo (see `Copilot Instructions` for the keyed-DI registration pattern), so swapping providers is a configuration choice, not a rewrite — but the choice has material consequences for media handling, latency, quota, and operational complexity.

Two realistic provider patterns are on the table:

1. **Azure AI VoiceLive** — a managed real-time voice service that exposes a single endpoint combining audio I/O, voice activity detection (VAD) including semantic VAD, barge-in/interruption handling, telephony codec support, function calling, and a TTS voice library. Designed to terminate the audio path itself and integrate with ACS media streaming with minimal app-side plumbing.
2. **OpenAI `gpt-realtime` (direct)** — go straight to the OpenAI Realtime API (or its Azure-deployed equivalent) over a WebSocket. The app owns the audio bridge: ACS bi-directional media streaming → app → model WebSocket, and back. The app also owns VAD, barge-in semantics, codec conversion, and any provider-specific session lifecycle.

In both cases the audio path on the ACS side is the **bi-directional media-streaming WebSocket** (see [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md), which decides DTMF capture independently). The difference is whether the audio terminates at a managed Microsoft endpoint (VoiceLive) or in app code that then forwards to the model.

The repo already contains both shapes: `RealtimeVoice.Azure` (VoiceLive-shaped) and the seam for additional providers via `LiveConversationClientBuilder`.

## Decision

**Pending.** The decision will be made after a benchtest comparing the two providers on the metrics below using a representative dialog corpus. Prior to that decision:

- The app **must** keep the realtime AI dependency behind `ILiveConversationClient` (keyed DI) so the choice can be deferred per-environment and changed without code churn.
- The reference deployment defaults to **Azure VoiceLive** (registered as the `voicelive` keyed conversation client in the Aspire `Showcase.AppHost`). This default is provisional and explicitly subject to change by this ADR.

### Decision drivers

| Driver | What to measure |
|---|---|
| **End-to-end latency** | First-token latency, p50/p95 turn-taking latency, barge-in responsiveness from the caller's perspective (PSTN ear, not server-side timestamps). |
| **Audio quality** | TTS naturalness, ASR accuracy on telephony-band audio (8 kHz µ-law/A-law upsampled), robustness to noisy lines and accents. |
| **Regional availability & data residency** | Where each service is GA, where call audio actually traverses, and which residency commitments apply for regulated workloads. |
| **Quota model** | Concurrent realtime sessions per region, throttling behaviour under burst, ability to reserve capacity. Affects [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) Tier-1 capacity planning. |
| **Function-calling model** | Sync vs async tool calls, parallel calls per turn, latency overhead, error semantics, compatibility with the existing `UseFunctionInvocation()` decorator. |
| **Operational surface** | Number of moving parts the app owns (audio bridge code, VAD, barge-in, reconnection, codec interop). VoiceLive minimises this; direct `gpt-realtime` maximises control. |
| **Observability hooks** | Native OpenTelemetry support, per-turn timing breakdown, easy correlation with ACS `correlationId` / `callConnectionId`. |
| **Cost per minute** | Combined media + model cost vs raw model cost; whether VoiceLive's bundled audio pipeline is cheaper than the equivalent self-built bridge once you include ACS media-streaming minutes. |
| **GA / model freshness** | How quickly new model revisions land in each surface, and how long behind GA the most recent capabilities are. |

## Consequences (provisional, regardless of which provider wins)

- The `ILiveConversationClient` abstraction stays. Any new provider is a new `LiveConversationClientBuilder` registration; the agent code path doesn't change.
- The DTMF decision in [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md) is **not** automatically determined by this one. If the realtime audio uses bi-di media streaming (true for either provider), DTMF *can* travel on the same WebSocket — but ADR-0007 weighs the trade-off independently.
- The graceful-degradation tiers in [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) **must** survive provider failure end-to-end; both options need a tested Tier-2/Tier-3 fallback that does not depend on the realtime model.
- Telemetry contract is provider-agnostic: `correlationId`, `serverCallId`, `callConnectionId`, plus a per-turn `realtimeProvider` and `sessionId` so dashboards can split by provider during the bake-off.
- Whichever provider wins, the loser is kept in repo behind the same abstraction as a tested fallback for regional incidents and as the comparison baseline for future re-evaluation.

### If the decision lands on **VoiceLive**

- App code that handles raw audio frames (bridge, resample, codec conversion, VAD, barge-in) is *not* written. The ACS media stream connects to VoiceLive, which connects to the model.
- Model freshness depends on Microsoft's release cadence on the VoiceLive surface, not on raw OpenAI release cadence.
- Function calling, semantic VAD, and interruption handling come from the platform; the app composes tools through the existing decorator pipeline.
- Quota planning follows VoiceLive concurrent-session limits per region.

### If the decision lands on **`gpt-realtime` direct**

- The app owns a `IRealtimeMediaBridge` component that pumps PCM frames between the ACS bi-di WebSocket and the model WebSocket, plus VAD, barge-in policy, jitter handling, and reconnection. This is non-trivial code and needs its own test surface (snapshots, fault-injection).
- Quota planning spans **two** services: ACS media-streaming sessions **and** `gpt-realtime` concurrent sessions. The lower of the two ceilings is the effective Tier-1 capacity for ADR-0008.
- The app gets the latest realtime model the day it ships.
- Telemetry must be instrumented in app code (no managed surface emitting it for you).

## Alternatives considered

- **Polling chat-completions style with discrete TTS/ASR (Azure Speech).** Rejected as the *primary* realtime experience — too much per-turn latency, no true barge-in, awkward turn-taking. It remains the foundation for [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) **Tier 2** (TTS + ASR for keyword/intent without a realtime model).
- **3rd-party realtime voice providers (Vapi, Retell, Deepgram Voice Agent, etc.).** Out of scope. The platform is committed to Microsoft 1st-party services for ACS interop, telemetry, and contractual reasons. They are not blocked architecturally — `ILiveConversationClient` could host one — but they are not part of this decision.
- **Run two providers concurrently per call (shadow mode).** Worth doing during the bake-off for a small percentage of calls, but rejected as a long-running production posture (doubles cost and adds a "which response wins" arbitration problem).
