# Runbook: Log Analytics and diagnostic-setting recovery

- **Status:** draft; not exercised against Azure
- **Last reviewed:** 2026-09-14
- **Owning team/role:** observability platform on-call
- **Related ADRs:** [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed)

## Purpose

Recover supported operational logs through either native workspace replication
or independent diagnostic destinations. These are different mechanisms; never
look for a separately manageable workspace for a native shadow replica.

## Triggers

`TelemetryFreshnessMissing`, `TelemetryQueryUnavailable`, or a relevant Azure
Service Health incident after [incident triage](high-availability.md) identifies
the LAW ingestion/query path. Also use for an approved replication or
dual-destination drill.

## Prerequisites

- Record which strategy is actually deployed and which tables/streams it covers.
- For branch A, replication was enabled **before** the incident; provisioning
  succeeded; the secondary contains enough history. Microsoft recommends at least
  one week before first switchover. Existing older logs are not backfilled.
- Verify the current [supported region pairs, feature exclusions, permissions,
  and API procedures](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/workspace-replication).
  In particular, East US, East US 2, and South Central US cannot replicate to one
  another. Dedicated-cluster replication must precede workspace replication.
- For DCR-based supported sources, verify association to the **system workspace
  DCE**, whose name is the workspace ID. Such a DCR must target only that workspace.
  This is not the same as an arbitrary DCE with a similar name.
- For branch B, both independent workspaces, destination settings, identities,
  table plans, retention, and query/rule configurations have been prepared.
- Verify recovery permissions in a drill. The source lists individual actions in
  its permissions table and additionally requires Log Analytics Contributor at
  **resource-group scope** for switchover; workspace-only access may fail.

## Safeguards

- Native replication is **not an end-to-end recovery procedure for Application
  Insights or Container Insights** under the current support matrix. Route those
  to their own runbooks. Auxiliary tables are not protected.
- Do not enable replication during an incident and describe it as historical DR.
- Do not disable replication to roll back a switchover; this can discard the
  secondary copy. Switchback is a separate operation.
- Workspace management changes, including retention, network settings, and new
  schema/provider onboarding, are blocked while switched over. Prestage them.
- Do not use an unassociated DCR as a replication cost-saving mechanism: the
  replication pricing warning says ingested billable data can still be charged
  even when incorrectly configured streams are not replicated.

## Diagnosis

1. Separate ingestion failure from query/RBAC failure and a regional issue from
   a cross-regional incident.
   **Expected:** a reason why switchover or alternate querying will help.
2. Compare a generated test record's emission/ingestion times in primary and
   secondary. For native replication, use the documented Logs Query API
   `overrideWorkspaceRegion=secondary` option to audit the inactive copy.
   **Expected:** useful replicated history and a measured lag, not only a
   successful configuration operation.
3. Inspect all source settings, DCR/DCE associations, network routes, and caps.
   **Expected:** a table-by-table coverage list; unsupported sources remain gaps.
4. Check the recovery runner and alert-rule region.
   **Expected:** the action and notifications do not rely solely on failed-region
   services.

## Mitigation

### Branch A: Native LAW replication

1. Obtain the incident commander's switchover approval and record the workspace
   identity and configured secondary region.
   **Expected:** one authorized operation against the intended environment.
2. Invoke the documented **workspace failover** operation in
   [Trigger switchover](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/workspace-replication#trigger-switchover).
   Use the supported CLI/REST procedure and API version from that page; do not
   construct a new workspace or edit all producer destinations.
   **Expected:** the long-running operation completes successfully.
3. Allow DNS propagation and inspect clients with long-lived connections.
   **Expected:** ingestion and queries use the secondary under the original
   logical workspace identity. A successful operation does not prove every
   client has changed routes.
4. Run a fresh canary for every supported source and activate preprovisioned
   secondary log-search alert rules if the rule-hosting region is affected.
   **Expected:** fresh data, successful queries, and actual notifications.
   Workspace replication does not replicate alert-rule resources.

### Branch B: Independent diagnostic destinations

1. If dual settings already send to both LAWs, leave the source unchanged and
   make the healthy workspace the authoritative query/rule target.
   **Expected:** its already-ingested copy is usable without a write to the
   affected source resource.
2. If only single ingestion exists, create or retarget an approved diagnostic
   setting on the exact producer scope using **Monitoring > Diagnostic settings**.
   Preserve unrelated settings; a resource supports at most five settings, and
   one setting can contain only one LAW destination.
   **Expected:** a read-back shows the selected categories and correct target.
3. For a global Front Door profile, global ACS/system topic, or multiregion Cosmos
   account, change the setting at the shared resource scope, not the current
   application region. For Redis connection events, inspect the database child
   scope; for Storage logs, the relevant service child resource.
   **Expected:** actual producer coverage, not an unused parent setting.
4. Generate fresh events and wait for source-specific configuration/ingestion
   latency. The generic diagnostic-settings documentation allows up to 90 minutes
   for first flow after creation; retargeting is not an instantaneous RTO promise.
   **Expected:** the target table receives fresh records. No automatic replay of
   the gap or migration of older records is assumed.
5. Update dashboards/rules and designate one authoritative copy.
   **Expected:** counts do not double when both destinations are queried.

## Verification

Query each critical table and source region, verify both event and ingestion
timestamps, test notifications, and compare a known event ID across copies.
Check resource-specific versus `AzureDiagnostics` table shape before reusing
queries. Preserve a list of inaccessible historical intervals.

For native replication, validate inactive/active region selection with the
documented query API and, where query auditing is enabled, `LAQueryLogs` region
fields. For dual ingestion, verify both distinct workspace identities directly.

## Rollback

- **Native:** verify primary Service Health, ingestion, query health, and complete
  reverse replication using `overrideWorkspaceRegion=primary`. Then invoke
  [Trigger switchback](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/workspace-replication#trigger-switchback).
  Monitor DNS/client convergence again. Returning too early can expose partial
  query results; reverse buffering is bounded, not indefinite.
- **Independent sinks:** restore the saved setting only after primary validation.
  Keep the alternate queryable and reconcile the split history. Remove temporary
  duplicate routing only after approval and parity checks.
- Record the final destination settings so later changes do not undo recovery.

## Escalation

Escalate to Azure support if provisioning/switchover fails, source-specific
permissions are ambiguous, or the secondary lacks expected data. Stop if the
strategy is unsupported, both regions are affected, or destination provisioning
would violate residency/network controls. Escalate to the incident commander
when expected recovery exceeds the approved window.

## Evidence capture

Use the [evidence template](../../evidence/templates/evidence-record.template.md):
record covered tables, exclusions, configured region pair, replication enablement
time, observed lag, operation/DNS timings, canary IDs, missing intervals, duplicate
counts, alert delivery, and failback results. Keep resource identifiers and
operation correlation details in approved private incident storage.

## Related ADRs

[ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md)
defines the proposed selection policy. Supporting references:
[diagnostic settings](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings)
and [Logs reliability](https://learn.microsoft.com/en-us/azure/reliability/reliability-monitor-logs).
