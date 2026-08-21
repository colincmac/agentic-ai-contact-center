# ADR-0003 — `IncomingCall` delivery via Event Grid webhook with synchronous validation handshake

- **Status:** Accepted
- **Date:** initial deployment

## Context

Once a call has been delegated to ACS (see [ADR-0001](0001-pstn-ingress-via-tpe.md)) and the agent app has chosen ACS Call Automation as its control plane (see [ADR-0002](0002-acs-call-automation-as-control-plane.md)), the app needs to be **notified** that a call has arrived so it can decide whether to answer, redirect, or reject it. ACS publishes a `Microsoft.Communication.IncomingCall` event for every inbound call. The supported delivery options for that event are:

1. **Event Grid system topic on the ACS resource → HTTPS webhook.** The app subscribes to the system topic and receives CloudEvents (or Event Grid events) at an HTTPS endpoint.
2. **Event Grid → Service Bus / Storage Queue / Event Hub.** The event is fanned into a queue the app polls/consumes asynchronously.
3. **Polling the ACS resource.** Not actually supported as a real-time mechanism; included only for completeness.

The `incomingCallContext` token included in the event is **opaque, signed, short-lived (~60 s), and effectively single-use** — whoever holds it can answer/redirect/reject *that specific call* until it expires. End-to-end timing is documented in [`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md). Event Grid also requires a **subscription validation handshake** before it will deliver real events; both synchronous (echo the validation code in the HTTP 200 body) and asynchronous (out-of-band GET to a one-time URL within 5 minutes) modes exist (see [`runbooks/event-grid-incomingcall-subscription.md`](../runbooks/event-grid-incomingcall-subscription.md)).

## Decision

- Deliver `Microsoft.Communication.IncomingCall` via an **Event Grid system topic on the ACS resource** to an **HTTPS webhook** on the agent app (`POST /events/incoming-call`).
- Use the **synchronous** validation handshake: detect `Microsoft.EventGrid.SubscriptionValidationEvent` (or the `aeg-event-type: SubscriptionValidation` header) and echo `validationCode` in a 200 response.
- Keep the `IncomingCall` webhook on a **different route** from the mid-call Call Automation callback URI (which receives mid-call CloudEvents — `CallConnected`, `PlayCompleted`, etc.). The two payloads are shaped differently and authenticated differently.
- The handler's **answer-window SLA is sub-second to a few seconds**; in no case may it exceed the `incomingCallContext` validity (~60 s wall-clock from event emission). Slow downstream dependencies must not block the answer decision.
- Configure a **dead-letter destination** (Storage container) on the Event Grid subscription so a webhook outage does not silently drop calls; failures are reconcilable from the dead-letter store and ACS-side CDRs.

## Consequences

- The `IncomingCall` webhook is internet-reachable and **cannot use the app's normal bearer-token auth** — Event Grid will not present one. Endpoint protection comes from a hard-to-guess path component, payload validation, and (preferred) Event Grid managed-identity / WebHook secret delivery options.
- The handler must **always** check for `SubscriptionValidationEvent` first and respond with the echoed `validationResponse` before treating any payload as a real event. Skipping this breaks the initial subscription provisioning and there is no failover (Event Grid retries the validation with exponential backoff for ~24 h, then marks the subscription `Failed`).
- The handler is on the critical path to call answering: any p95 latency over a few seconds eats into the ~60 s `incomingCallContext` window and increases the chance of dropped calls. Heavy work (warming AI sessions, looking up CRM context) must happen **after** `AnswerCall` is invoked, not before.
- Event Grid delivers **at-least-once** with retries on a 10 s → 30 s → 1 m → 5 m → 10 m → 30 m → 1 h → 3 h → 6 h → 12 h backoff schedule and a default 24-hour TTL ([`runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md)). For `IncomingCall` specifically, retries beyond the `incomingCallContext` TTL are useless to act on but still valuable for telemetry/CDR — the dead-letter destination captures these.
- The same at-least-once semantics apply to mid-call Call Automation callbacks. The app's idempotency contract (dedup on `(callConnectionId, sequenceNumber)`) is captured in [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md).
- The **circuit-breaker pattern** for the `IncomingCall` handler is to fail fast to `RedirectCall` (e.g., to a safe Auto Attendant or "we're experiencing issues" fallback) rather than holding the `incomingCallContext` and letting it expire. This is the entry point that ADR-0008's "Tier 4" graceful degradation hooks into.

## Alternatives considered

- **Event Grid → Service Bus queue → app worker.** Rejected for the `IncomingCall` event: adds a queue hop on the critical path, and a worker draining a queue cannot meaningfully act on an `incomingCallContext` that may already be near expiry. Service Bus / Event Hubs remain reasonable for non-time-critical fan-out (analytics, recording triggers), but not for the primary answer path.
- **Asynchronous validation handshake.** Rejected as the default — requires a human/scripted out-of-band GET against a validation URL within 5 minutes, which is operationally painful and adds risk during automated environment provisioning. Kept as a known-good fallback for cross-tenant or proxied deployments where the synchronous response cannot reach Event Grid.
- **Polling the ACS resource.** Not a supported real-time mechanism and would not produce an `incomingCallContext` to act on. Out of scope.
- **One combined webhook for `IncomingCall` and mid-call callbacks.** Rejected — different payload shapes, different auth posture, and different retry/SLA characteristics. Splitting the routes keeps each handler small and independently observable.
