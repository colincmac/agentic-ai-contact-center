# Runbooks

Operator procedures for common day-2 tasks and failures.

| Runbook | Covers |
| --- | --- |
| [`event-grid-incomingcall-subscription.md`](event-grid-incomingcall-subscription.md) | Event Grid subscription validation handshake for the `IncomingCall` webhook — synchronous vs asynchronous response modes, the 5-minute window for the async URL, the ~24h validation retry behaviour, and the four things every handler must implement (validation envelope detection, sync echo, no-bearer-token endpoint protection, validationCode logging). |
| [`timing-and-retries.md`](timing-and-retries.md) | The full ACS Call Automation / Event Grid timing model: delivery retry backoff (10s → 12h over a 24h TTL), the ~60s `incomingCallContext` answer window, `Recognize` timeout structure, application-level retry / escalation policy, transfer / `AddParticipant` timing, idempotency on at-least-once delivery, suggested defaults for every tunable, and the production hardening checklist (dead-letter queue, dedup cache, circuit breaker, per-leg correlation, synthetic probers). |
| [`teams-configuration/teams-extensibility.md`](teams-configuration/teams-extensibility.md) | Teams Phone Extensibility overview and official quickstart links. |
| [`teams-configuration/tpe-onboarding-guide.md`](teams-configuration/tpe-onboarding-guide.md) | Greenfield enterprise onboarding for a Teams resource account, Entra application, ACS, Bot Service, and Event Grid. |
| [`teams-configuration/tpe-brownfield.md`](teams-configuration/tpe-brownfield.md) | Brownfield onboarding when the Teams resource account or ACS resources already exist. |
| [`monitoring/high-availability.md`](monitoring/high-availability.md) | Start here for monitoring outages: identify the failed path, choose a recovery procedure, and verify data and notifications. |
| [`monitoring/log-analytics-recovery.md`](monitoring/log-analytics-recovery.md) | Draft LAW native switchover/switchback and independent diagnostic-destination recovery. |
| [`monitoring/aks-telemetry-recovery.md`](monitoring/aks-telemetry-recovery.md) | Draft Container Insights, managed Prometheus, and Advanced Container Networking telemetry recovery. |
| [`monitoring/application-insights-retargeting.md`](monitoring/application-insights-retargeting.md) | Draft application/collector and APIM Application Insights destination changes, canary rollout, and rollback. |
| [`monitoring/power-platform-telemetry-recovery.md`](monitoring/power-platform-telemetry-recovery.md) | Draft Dynamics/Power Platform export recovery, latency expectations, and manual administration boundaries. |

The HA/DR runbooks above are researched drafts, not exercised production
procedures; see [ADR-0017](../adr/0017-telemetry-high-availability-and-disaster-recovery.md).
The other service-named files under `monitoring/` remain placeholders and do not
provide operational coverage. Still to come: the operator playbook for
[ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md)'s degradation
tiers, dashboard triage, region drain, and service-specific monitoring alerts.
