# Runbook: Recover monitoring during an outage

- **Status:** draft reference procedure; adapt and test for your environment
- **Last reviewed:** 2026-09-17
- **Owning team/role:** monitoring owner and incident coordinator
- **Related ADR:** [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed)

## Purpose

Identify which monitoring path has failed and choose the right recovery
procedure. Use this alongside the
[HA/DR options guide](../../monitoring/high-availability.md).

**First principle:** a blank dashboard does not necessarily mean data collection
has stopped, and a monitoring outage does not necessarily mean the workload failed.

## Triggers

Use this runbook for a relevant Azure Service Health incident, an unexpected loss
of monitoring data, or a planned recovery exercise.

These example alert names and thresholds are starting points, not service
guarantees. Adjust them to your normal traffic and collection delay.

| Example alert | Starting condition |
| --- | --- |
| `TelemetryFreshnessMissing` | A known test event is absent in two consecutive five-minute windows while its source remains healthy |
| `TelemetryQueryUnavailable` | Queries fail for five minutes from two independent locations |
| `PrometheusScrapeMissing` | Expected series are absent for three scrape intervals while the cluster is reachable |
| `TelemetryDestinationDrift` | A sender still uses the old destination after the planned configuration-change window |

Do not apply these short thresholds to Dynamics/Power Platform exports. Use the
[Power Platform procedure](power-platform-telemetry-recovery.md), which accounts
for their longer delivery window.

## Prerequisites

Before an incident, record:

- The owner of each telemetry path and who can approve recovery changes.
- The primary and alternate destination or archive, including region,
  permissions, connectivity and retained history.
- The acceptable data loss and time to restore queries and notifications.
- A safe test event or request, expected arrival time and rollback settings.

Confirm that recovery access and credentials remain available outside the failed
region. An alternate destination is useful only if you can send to and query it.

## Safeguards

- Change monitoring configuration only where needed. Do not change workload
  traffic routing or interrupt active calls just to test logging.
- Save settings and UTC timestamps before changes.
- Do not delete workspaces, reduce retention, purge data or disable replication
  as an incident workaround.
- Keep test data free of customer content, credentials and personal identifiers.
- Use one change owner so concurrent recovery actions do not conflict.

## Diagnosis

1. **Check the workload independently.** Test it without relying on its dashboard.
   **Expected:** identify whether the workload, monitoring, or both are affected.
2. **Check service health and access.** Inspect Azure Service Health, destination
   health, permissions, network access, throttling and ingestion caps.
   **Expected:** distinguish a service outage from a local configuration problem.
3. **Follow one test event.** Confirm it was generated, then check collection,
   ingestion and a direct query.
   **Expected:** identify the failing step rather than changing every destination.
4. **Test the alternate.** Query existing data and verify fresh test data where
   the chosen strategy supports it.
   **Expected:** confirm the alternate has usable history, capacity and access.

## Mitigation

Choose the row matching the failed path. Leave healthy collection paths unchanged.

| Affected path | Action | Expected result |
| --- | --- | --- |
| Supported LAW logs with native replication | Follow [LAW recovery, branch A](log-analytics-recovery.md#branch-a-native-law-replication) | Customer-initiated switchover restores the same logical workspace through its secondary |
| Diagnostic logs with independent LAW destinations | Follow [LAW recovery, branch B](log-analytics-recovery.md#branch-b-independent-diagnostic-destinations) | Queries and alerts use the healthy copy, or new data reaches a changed destination |
| Historical logs protected only by an archive | Use the documented archive access/replay procedure for your chosen destination | Selected history becomes accessible; immediate LAW query recovery is not assumed |
| Container Insights, Prometheus or network telemetry | Follow [AKS telemetry recovery](aks-telemetry-recovery.md) | The affected log or metric collection path is restored |
| Application Insights or APIM logger | Follow [Application Insights retargeting](application-insights-retargeting.md) | Fresh telemetry reaches the alternate resource and independent LAW |
| Dynamics or Power Platform export | Follow [Power Platform recovery](power-platform-telemetry-recovery.md) | Export settings are corrected and delivery is checked against the appropriate delay |
| Dashboard or alerting only | Use prepared alternate Grafana/direct queries and secondary rules | Existing ingestion is preserved while access and notifications recover |

After each action, record which copy is used for queries and paging. State any
missing sources or historical intervals; one working chart is not full recovery.

## Verification

Before declaring recovery:

- Query a fresh test event or metric from each affected source.
- Check its source/region identity, timestamps and correlation fields.
- Test a notification through the active alert rule and action group.
- Confirm operator access and that duplicate copies do not double-count or page.
- Record time to restored ingestion, queries and notifications separately.

## Rollback

Use the selected procedure's rollback steps. Verify primary health before moving
back, restore a small group first, then repeat the checks above. Keep both
destinations available for historical investigation. Avoid automatic switching
back and forth while the primary remains unstable.

## Escalation

Contact the incident coordinator and service owner if recovery affects the
workload, exceeds the agreed recovery time, or requires an unapproved region or
configuration. Contact Azure support for sustained monitoring service failures
and Power Platform/Dynamics support for export failures.

Stop changes if the alternate is unhealthy, required access is missing, or the
recovery mechanism is unsupported.

## Evidence capture

Record the timeline, affected sources, settings changed, observed gaps or
duplicates, notification result and rollback outcome. Use the
[evidence template](../../evidence/templates/evidence-record.template.md) for a
recovery exercise. Keep sensitive incident details in your approved private
record system.

## Related ADRs

[ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md)
explains the proposed regional collection and selective protection pattern.
