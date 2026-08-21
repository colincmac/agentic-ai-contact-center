# Conversation strategies

The design assigns each active call exactly one `IConversationStrategy` instance — the "brain" of the call. The strategy consumes caller input (audio frames, DTMF tones), emits outbound directives (audio, verbs, transfers), and publishes structured `StrategyEvent`s for observers and dashboards.

This folder documents the strategy contracts imported from the source showcase,
what they emit, and how they hand off information during workflow step changes
and degradation events. The corresponding implementation has not yet been
migrated into this repository's `code/` folder.

| Document | Strategies covered |
| --- | --- |
| [`conversation-strategies.md`](conversation-strategies.md) | All strategies (Realtime voice, NLU, DTMF, Composite) and the handoff model |

## Companion ADRs

- [ADR-0008 — Graceful degradation: Realtime → DTMF](../adr/0008-graceful-degradation-realtime-to-dtmf.md)
- [`architecture/call-flow.md`](../architecture/call-flow.md) — where strategies sit relative to ACS Call Automation and the call edge
