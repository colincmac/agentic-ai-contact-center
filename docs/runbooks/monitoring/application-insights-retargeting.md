# Runbook: Application Insights destination retargeting

- **Status:** draft; runtime and APIM cutover not exercised
- **Last reviewed:** 2026-09-14
- **Owning team/role:** application on-call, observability platform, and APIM owner
- **Related ADRs:** [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed), [ADR-0010](../../adr/0010-active-active-multi-cluster-topology.md)

## Purpose

Restore future application/APIM telemetry to a preprovisioned alternate
Application Insights component with an independent backing LAW. This is not
historical data migration, LAW native switchover, or a Dynamics export procedure.

## Triggers

`TelemetryFreshnessMissing`, `TelemetryQueryUnavailable`, or
`TelemetryDestinationDrift` from the [incident entry point](high-availability.md),
after confirming application/APIM telemetry is affected. Also use for an approved
destination-change drill.

## Prerequisites

- Complete inventory of direct SDK exporters, OTLP collectors, hosting
  environment settings, App Configuration keys/labels/stores, browser telemetry
  if present, APIM loggers, and API/service-level diagnostics overrides.
- Independently provisioned target Application Insights **and** LAW. Verify
  component/workspace regions, data residency, retention, ingestion limits,
  query access, network controls and authentication for the actual producer.
- Existing workload identity or other supported authentication configured for
  the alternate destination. A connection string chooses a component; it does
  not by itself grant Entra ingestion permissions.
  For APIM managed-identity logging, assign Monitoring Metrics Publisher on the
  alternate **Application Insights resource**, not the Prometheus DCR scope.
- A saved configuration version, one harmless synthetic transaction, direct
  access to both query scopes, and approved rollback criteria.
- For APIM, the deployed tier/topology and effective logger/diagnostic policy at
  service and per-API scope. Distinguish separate regional services from one
  service with multiple regional gateways. APIM workspace loggers have a
  distinct support/configuration scope.
- Read the [Application Insights reliability guide](https://learn.microsoft.com/en-us/azure/well-architected/service-guides/application-insights)
  and [APIM integration procedure](https://learn.microsoft.com/en-us/azure/api-management/api-management-howto-app-insights)
  before adapting this runbook to a particular SDK or gateway.

## Safeguards

- Native LAW replication's support matrix excludes Application Insights over
  LAW; do not substitute that operation for component/ingestion recovery.
- Do not repoint every region because only one producer or network path is
  unhealthy. Do not change sampling, correlation fields, or auth policy during
  the same incident unless required and approved.
- Never log the full connection string, tokens, request bodies, OTPs or caller
  identifiers as cutover evidence.
- Do not restart voice pods indiscriminately: WebSocket-bound calls can terminate.
  A safe call-aware drain is a workload operation requiring its owner's approval.
- Changing the linked LAW on an existing component is not equivalent to creating
  an independent component, and does not move its old records.
- Diagnostic export from Application Insights to another LAW is a downstream
  copy, not an independent Application Insights ingestion endpoint. Also follow
  Microsoft's [duplicate-data access restrictions](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings#diagnostic-settings-for-application-insights)
  when enabling that option.
- APIM All APIs plus per-API loggers do not automatically produce two copies:
  the per-API logger normally overrides the other. Multiplexing with different
  loggers requires Microsoft Support enablement. Do not rely on lossless
  self-hosted gateway delivery; the integration guide warns about in-memory
  buffering.

## Diagnosis

1. Emit a test event and inspect SDK/collector delivery errors, buffering, caps,
   DNS/egress and authentication. Check Service Health independently.
   **Expected:** evidence of a target-path problem rather than no application
   activity or a missing instrumentation registration.
2. Query the component and backing LAW directly, outside Grafana.
   **Expected:** ingestion failure is separated from dashboard/query-scope issues.
3. Inspect the running producer's selected routing version and configuration
   source. Determine whether exporter options are bound at startup.
   **Expected:** a specific update and reload/rollout mechanism for each consumer;
   no assumption that a refreshed key means a refreshed exporter.
4. Send a canary through the alternate using the intended producer identity.
   **Expected:** fresh queryable data at the independent target, with expected
   role/region and correlation fields.

## Mitigation

### Applications and collectors

1. Approve the affected region/cohort and acquire single-writer change ownership.
   Save previous settings, routing version and UTC cutover boundary.
   **Expected:** rollback state and conflict protection.
2. If the producer uses App Configuration, update its approved region/label or
   snapshot-based routing configuration; distribute to each independent store
   that needs the change. Otherwise update its actual environment, secret
   reference, collector config or deployment setting.
   **Expected:** configuration read-back shows the complete alternate destination
   and new version without exposing live values in logs.
3. Refresh configuration using the supported provider mechanism. Reinitialize the
   exporter only if verified for that SDK; otherwise perform an approved canary
   rollout. For voice workloads, use call-aware drain or a validated independent
   collector change rather than terminating live calls.
   **Expected:** runtime destination changes, not merely configuration storage.
4. Emit a synthetic transaction and query new logs, traces and metrics for that
   cohort. Inspect buffering/retry errors and normal workload health.
   **Expected:** current data at the alternate without dropped calls or repeated
   export failures.
5. Expand in bounded cohorts, stopping if data or application health deteriorates.
   **Expected:** all intended producers report the approved routing version.
6. Change dashboard/rule scopes and maintain old/new historical query access.
   **Expected:** one authoritative paging path and a recoverable split timeline.

### API Management Application Insights logger

1. Inventory existing logger resources and effective service/API diagnostics;
   preserve sampling, correlation protocol, verbosity, error logging, and
   header/body allowlists.
   **Expected:** no API-specific override remains hidden.
2. Preconfigure or update a supported Application Insights logger for the
   alternate component, with any required identity/named-value references.
   Use the documented REST/ARM configuration for **connection string plus
   managed identity**. The portal's connection-creation flow currently uses an
   instrumentation key; do not assume it creates the recommended identity
   configuration.
   **Expected:** the logger's destination and authentication are valid.
3. Update the effective diagnostic configuration to reference the alternate
   logger where required, at service scope and for APIs with overrides.
   A new logger by itself is not sufficient if diagnostics still reference the
   old one.
   In the portal's API settings, inspect **Diagnostics Logs > Destination** for
   All APIs and each overriding API/version, or use the corresponding
   service/API diagnostic management API. Preserve sampling and payload limits.
   **Expected:** configuration read-back identifies the alternate on every
   intended gateway/API scope.
4. Exercise a harmless request through each relevant gateway/region/API and
   check the alternate Application Insights data.
   **Expected:** fresh gateway telemetry with correct correlation and configured
   sampling; no broad payload capture is introduced.
5. Leave **APIM resource diagnostic settings** alone unless that separate LAW
   path also needs recovery via [LAW recovery](log-analytics-recovery.md).
   **Expected:** no confusion between platform logs and the AI logger pipeline.

If APIM is behind private-only AMPLS ingestion, verify that **both** the alternate
Application Insights component and its LAW are included and reachable. Do not
resolve the problem by broadly opening public ingestion.

## Verification

- Verify actual arrival, role/region, event/ingestion times and routing version.
- Trace a synthetic request across APIM and application; include a test failure
  as well as a success when safe.
- Query the old and new destinations for a transaction crossing cutover.
  Preserve event/span identifiers; a call ID alone is not a deduplication key.
- Verify dashboard/rule access and a real notification at the alternate.
- Record which queued records arrived late, remained at the old destination,
  duplicated, or were lost. SDK buffering/retry is bounded and implementation
  dependent; do not assume all queued batches follow a new connection string.

## Rollback

Restore the saved routing version/configuration through the same conditional
change process, and repeat the safe canary reload/rollout. Restore APIM diagnostic
logger references at all modified scopes. Verify fresh data before expanding.
Keep both components and LAWs for historical reconciliation; do not delete or
relink them as cleanup during an incident. Record the final configuration so
later changes preserve it.

## Escalation

Escalate to the workload owner if exporter reload requires unsafe pod termination,
to the APIM owner for tier/scope/identity propagation issues, and to Azure support
for sustained ingestion/query failure. Stop if the alternate shares the failed
dependency, fails canary validation, or lacks approved residency/access.
SaaS export issues use the [Power Platform runbook](power-platform-telemetry-recovery.md).

## Evidence capture

Use the [evidence template](../../evidence/templates/evidence-record.template.md).
Record SDK/collector versions, configuration source and reload behavior,
sanitized routing versions, API scope coverage, canary IDs/times, buffer behavior,
gap/duplicate intervals, workload impact, alert results and rollback.
Explicitly distinguish a successful configuration update from measured recovery.

## Related ADRs

- [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md):
  proposed source-specific destination strategy.
- [ADR-0010](../../adr/0010-active-active-multi-cluster-topology.md):
  regional workloads and voice-call migration limits.
