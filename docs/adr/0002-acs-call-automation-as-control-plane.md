# ADR-0002 — ACS Call Automation as the call control plane

- **Status:** Accepted
- **Date:** initial deployment

## Context

Once a call has landed inside Azure Communication Services (see [ADR-0001](0001-pstn-ingress-via-tpe.md)), something has to **control** the call: answer/reject, play prompts, recognize input, transfer, hang up, mix participants, and observe lifecycle events. The plausible options are:

1. **ACS Call Automation** — a 1st-party Azure service exposing the call as an HTTPS REST API plus mid-call CloudEvents posted to a callback URI. The app is *not* in the media path; ACS handles RTP/SRTP, codecs, DTMF normalisation, and TTS/ASR via a linked Cognitive Services resource.
2. **An in-app SIP stack / SBC integration** — terminate SIP and RTP inside the app (or beside it on an SBC), implement INVITE/REFER/BYE, run a media engine (PJSIP, FreeSWITCH, Asterisk, or a managed equivalent), and bridge to AI components yourself.
3. **Teams Calling APIs (Graph)** — drive the call through the Microsoft Graph cloud-communications endpoints. Workable when the agent identity is a Teams app, but the surface is narrower for IVR-style `Play`/`Recognize` and is not the supported path once TPE has delegated the call to ACS.

The reference architecture, sequence diagrams, timeouts, and idempotency contract in [`call-flow.md`](../architecture/call-flow.md) (with detailed wire diagrams in [`sequence-diagrams.md`](../architecture/sequence-diagrams.md) and timing tables in [`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md)) all assume the app speaks **HTTPS + CloudEvents** to ACS — never SIP.

## Decision

- Use **ACS Call Automation** as the sole call-control plane for inbound calls.
- The agent app exposes two HTTPS endpoints: an **Event Grid webhook** for `Microsoft.Communication.IncomingCall` (see [ADR-0003](0003-incomingcall-delivery-via-event-grid.md)) and a **mid-call callback URI** for ACS Call Automation CloudEvents (`CallConnected`, `PlayCompleted`, `RecognizeCompleted`/`Failed`, `CallTransferAccepted`/`Failed`, `CallDisconnected`, participant events, etc.).
- The app drives the call exclusively through `CallAutomationClient` / `CallConnection` / `CallMedia` SDK calls — `AnswerCall`, `RedirectCall`, `RejectCall`, `PlayToAll`/`Play`, `StartRecognizing`, `TransferCallToParticipant`, `AddParticipant`/`RemoveParticipant`/`HoldParticipant`/`UnholdParticipant`, `HangUp`.
- The app is **not** in the media path for the standard control flow. (Live audio access, when needed for realtime AI, is a separate decision — see [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md) and [ADR-0007](0007-dtmf-bidirectional-websocket-vs-callback-api.md).)
- **No SIP stack and no SBC** ship in the agent app.

## Consequences

- The app's runtime contract is small and HTTP-shaped: two webhooks plus REST/SDK calls. It can run as ordinary stateless containers on AKS, behind a standard ingress; no special UDP handling, no STUN/TURN, no codec licensing.
- Cross-cutting concerns (auth, retries, telemetry) follow the project's existing decorator-pipeline pattern around `IRealtimeAgent`/`ILiveConversationClient` (see repo `Copilot Instructions`), since Call Automation is consumed as just another HTTP+SDK dependency.
- Every Play / Recognize / Transfer call accepts an `operationContext` string. The app **must** set this on every operation so the resulting callback identifies which menu node, prompt, or transfer attempt it belongs to. This is how callbacks correlate back to in-app state (see [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)).
- TTS and speech recognition require the ACS resource to have a **linked Cognitive Services** connection (used by `TextSource` prompts and `Recognize` speech modes). This becomes a deployment prerequisite captured in IaC.
- The mid-call callback URI is internet-reachable. It must be authenticated (per-call signing secret in the path, HMAC, or Event Grid managed-identity delivery) — see [`call-flow.md` "Things worth being deliberate about"](../architecture/call-flow.md).
- Support escalations that span the Teams side and the ACS side need both `correlationId` and `serverCallId` (from the `IncomingCall` payload) plus `callConnectionId` (from Call Automation) on every log line.
- We accept the platform's feature ceiling: anything ACS Call Automation does not yet expose (e.g., specific advanced media operations) is unavailable to the app until ACS adds it. This is a deliberate trade for not running an SBC.

## Alternatives considered

- **Self-hosted SIP/RTP stack (PJSIP, FreeSWITCH, Asterisk, or managed equivalent).** Rejected. Combined with ADR-0001's TPE choice, the Teams↔ACS handoff is internal Microsoft delegation — the app never sees SIP anyway. Adding a SIP stack would only matter if we also dropped TPE and ran our own SBC, which ADR-0001 already decided against for the standard path.
- **Microsoft Graph cloud-communications calling APIs.** Rejected for the standard inbound-IVR path. Once TPE delegates to ACS, the call object lives in ACS; using Graph to drive a parallel Teams-side calling bot would either duplicate the call control or fight ACS for the call. Graph remains relevant for Teams-app-only scenarios outside this architecture.
- **A 3rd-party CPaaS (Twilio, Vonage, etc.).** Rejected — would require leaving the Microsoft PSTN/Teams ingress entirely and undermines the Dynamics 365 Contact Center escalation path (which is itself ACS-based, so an ACS-to-ACS transfer is cheaper and richer than an external CPaaS handoff).
