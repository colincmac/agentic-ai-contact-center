# ADR-0005 — Escalation transfer model: blind `TransferCallToParticipant` is the default; consultative `AddParticipant` + `RemoveParticipant` reserved for VIP / supervisor handoffs

- **Status:** Accepted
- **Date:** initial deployment

## Context

When the AI agent decides to escalate a call (IVR couldn't resolve the intent, the caller asked for a human, fraud signal triggered, etc.) it hands the call off to a downstream destination — typically a Dynamics 365 Contact Center workstream queue (which is itself ACS-based), but possibly a Teams agent or a partner queue exposed as a SIP URI.

ACS Call Automation exposes two fundamentally different transfer models, both documented end-to-end with sequence diagrams in [`architecture/transfer-patterns.md`](../architecture/transfer-patterns.md):

1. **Blind transfer** — `CallConnection.TransferCallToParticipant(target, customCallingContext)`. ACS issues the equivalent of a SIP REFER under the hood; on `CallTransferAccepted` the agent app **drops out of the call** and is no longer in the media or control path. Failure is signalled by `CallTransferFailed`.
2. **Consultative (attended) transfer** — `AddParticipant(agent)` to bring the destination into the existing call, optionally `HoldParticipant(caller)` for a private brief, then `RemoveParticipant(self)` once the agent is ready. The IVR (or a supervisor bot) **stays in the call** during the briefing window and only leaves explicitly. Three-party media is required during the consultation phase.

The trade-off table is captured in the appendix:

| Aspect | Blind | Consultative |
|---|---|---|
| IVR stays in call during handoff | No | Yes |
| Can brief the agent | No (UUI / screen-pop only) | Yes (live audio briefing) |
| Caller experience on agent reject | Lost unless `CallTransferFailed` is handled | Stays on hold; never disconnected |
| Cost / complexity | Lower | Higher (3-party media + per-leg events) |
| Best for | High-volume, well-known queues | VIP, complex escalations, supervisor takeover |

## Decision

- **Blind `TransferCallToParticipant` is the default escalation path.** All standard intents that escalate route this way.
- IVR-collected context (intent, collected digits, language, `correlationId`, biometrics outcome, etc.) is passed to the destination using the **`customCallingContext`** parameter (SIP custom headers / X-MS-Custom-* user-to-user info) so the receiving workstream can do screen-pop and skill-based routing without re-asking the caller.
- A **`CallTransferFailed` handler is mandatory.** It must, at minimum, play a "we're unable to connect you right now" prompt and either re-queue, route to voicemail, or hang up cleanly — never silently drop the caller.
- **Consultative transfer (`AddParticipant` + `RemoveParticipant`) is opt-in** and reserved for:
  - VIP routing where a warm intro is required.
  - Supervisor takeover during a live agent call.
  - Workflows that need a programmatic "brief the agent" step (e.g., a supervisor bot reads collected context aloud, or an agent confirms readiness).
- Per-call selection between blind and consultative is driven by the dialog/intent configuration, not by global feature flags. The same agent app supports both.

## Consequences

- The standard escalation path is the **simpler** one: fewer events, no three-party media management, lower per-call cost. Aligns with the "high-volume, well-known queues" sweet spot of typical contact-center traffic.
- Dynamics 365 Contact Center (the primary escalation destination) accepts `customCallingContext` headers and uses them for screen-pop, so the blind path delivers a rich agent experience without needing the consultative model.
- Failure handling is **non-optional and observable**. `CallTransferFailed` rates are a key SRE signal — a spike usually means a downstream queue is misconfigured, closed, or rejecting the custom headers.
- Consultative flows require:
  - Carrying additional state in Redis ([ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)) for the consultation phase (`participants`, `consultationActive`, `pendingAgent`).
  - Hold-music handling (`PlayToAll(holdMusic, loop=true)` against the caller while the brief happens) — see [`architecture/transfer-patterns.md`](../architecture/transfer-patterns.md).
  - Explicit handling of the agent-abandons-mid-consult failure mode (re-unhold caller, re-prompt, loop or fall back to blind).
- Blind transfer drops the agent from the media path the moment `CallTransferAccepted` fires. Any post-transfer telemetry the app needs (call duration on the destination side, agent disposition) must come from the destination system, not from ACS.
- Both models share the same `customCallingContext` propagation, so promoting a flow from blind to consultative does not change what context the destination ultimately sees.

## Alternatives considered

- **Consultative-by-default.** Rejected. Adds three-party media and significantly more events on every escalation, when the vast majority of escalations are routine handoffs to a CCaaS queue where the screen-pop already carries everything the agent needs. Blind is cheaper and simpler for the common case.
- **`RedirectCall` instead of `TransferCallToParticipant`.** Rejected for *escalations* — `RedirectCall` only applies before answering, so it is not a tool for in-call handoff. It is, however, the right tool for the load-shedding fallback in [ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md) Tier 4.
- **Always-on supervisor bot (third party in every call).** Rejected. Adds cost and complexity to every call to support an exception scenario; the consultative pattern delivers the same capability on demand.
- **Out-of-band screen-pop (REST call to CCaaS in parallel with blind transfer).** Possible as an enrichment, but not a substitute — `customCallingContext` is the supported path and survives the transfer atomically. An out-of-band screen-pop introduces a new race condition (transfer arrives before the screen-pop). Out of scope for the default flow.
