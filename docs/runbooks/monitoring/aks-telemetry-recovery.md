# Runbook: AKS logs, Prometheus, and network telemetry recovery

- **Status:** draft; collector and region recovery not exercised
- **Last reviewed:** 2026-09-14
- **Owning team/role:** Kubernetes platform and observability on-call
- **Related ADRs:** [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed), [ADR-0010](../../adr/0010-active-active-multi-cluster-topology.md)

## Purpose

Restore cluster telemetry using its actual ingestion path. AKS resource
diagnostic settings, Container Insights logs, managed Prometheus metrics,
application OTel, and ACNS flow data are not the same pipeline.

## Triggers

`PrometheusScrapeMissing`, `TelemetryFreshnessMissing`, or an ACNS dashboard with
missing network series while the affected cluster is reachable. Also use for a
planned loss-of-destination exercise. See [incident triage](high-availability.md)
for proposed threshold assumptions.

## Prerequisites

- Access to each cluster from a healthy region, and read access to agents,
  configuration, DCR/DCE associations, LAW/AMW, metric rules and Grafana.
- Exact current add-on/agent versions, networking mode, supported region/SKU,
  cluster aliases/labels, scrape configuration, and log collection configuration.
- Alternate destination preprovisioned with identity, private endpoint/DNS,
  ingestion/query capacity and appropriate data collection resources.
- Saved collection settings, approved critical series/tables, canary workload,
  and observation windows based on scrape interval and log latency.
- The [source-specific design](../../monitoring/high-availability.md) and a
  completed inventory of the collection paths in your environment.
- For console-log multihoming, complete [high-scale onboarding](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-high-scale)
  and validate the selected [region mapping](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-region-mapping).
  The documented exception is namespace-based stdout/stderr collection, not
  automatic duplication of every Container Insights stream.
- For ACNS stored logs, verify Cilium/Kubernetes 1.33+, feature/tooling
  availability and at least one `ContainerNetworkLog` resource using the
  [stored-log setup](https://learn.microsoft.com/en-us/azure/aks/how-to-configure-container-network-logs).

## Safeguards

- Do not disable all monitoring, reboot nodes, or restart voice workloads as the
  first diagnostic action.
- Do not route Prometheus data to LAW or container logs to AMW.
- Do not apply a generic AMA multi-workspace DCR recipe to the Container Insights
  add-on without explicit product support.
- Do not rely on LAW native replication for Container Insights end-to-end
  recovery under the current support matrix.
- Keep scrape labels stable and bounded. No phone numbers, per-call IDs, or
  unbounded request/URL labels.
- A secondary receiver protects only samples/logs successfully sent to it; it
  cannot scrape a dead cluster or restore unflushed node-local buffers.
- Preserve networking/security policies; test private connectivity instead of
  broadly enabling public access.
- Do not delete an AMW as part of troubleshooting: metric data has no soft-delete
  recovery. Do not promise arbitrary cross-region console multihoming or native
  AMW regional failover without the outstanding support/drill validation.

## Diagnosis

1. Check cluster/node/workload health independently of Grafana.
   **Expected:** a workload outage is separated from missing telemetry.
2. Identify the absent signal and its path:

   | Signal | Inspect | Correct sink |
   | --- | --- | --- |
   | API server/audit/control-plane logs | AKS resource diagnostic settings | LAW |
   | Container stdout/stderr, inventory/events | Container Insights agent and collection configuration/DCR | LAW |
   | Scraped cluster/application metrics | Managed Prometheus agent, scrape configuration, DCR/DCE/associations | AMW |
   | ACNS aggregate network metrics | ACNS collection and Prometheus scrape targets | AMW when configured |
   | ACNS flow data | Actual configured flow collector/output, not just the metrics add-on | Its configured supported log/storage destination |
   | Application traces/logs | Application or OTLP collector exporter | Application Insights or configured backend |

   **Expected:** one identified failing pipeline, not a generic "AKS logs" change.
3. Check agent health/errors, scrape targets, authentication, egress, DCE
   reachability, workspace limits and direct queries.
   **Expected:** source collection failure is separated from target ingestion/
   query failure.
4. Check the **location** of each DCR/DCE/AMW, not just its name. Separate
   per-cluster collection resources can still share a monitoring region.
   **Expected:** actual shared regional dependencies are visible.

## Mitigation

### Container Insights logs

1. If only the dashboard/query path failed, query the current LAW directly or the
   already-provisioned supported alternate copy. Leave healthy collectors alone.
   **Expected:** no avoidable collection interruption.
2. If the agent failed, repair its approved collection configuration, identity or
   network path using the supported AKS monitoring workflow.
   **Expected:** healthy collector status and fresh logs at the intended LAW.
3. If validated console-log multihoming already exists, inspect each
   `ContainerLogV2Extension` DCR/DCE and use the healthy copy for the configured
   namespaces. If establishing this during a planned exercise, follow the
   [multihoming procedure](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-multitenant):
   enable high-scale/multitenancy, create one extension DCR per destination with
   the same namespace selection, and associate each with the cluster.
   **Expected:** both copies receive markers after the documented five-minute
   DCR-discovery interval plus ingestion latency; no more than 30 extension DCR
   associations, and unmatched namespace fallback is verified.
4. For default streams, if a destination change is necessary, use the supported Container Insights
   workspace-change/onboarding procedure for the installed add-on, preserving
   ConfigMap collection filters and validating the resulting DCR associations.
   Treat any required offboard/re-onboard interval as an ingestion gap.
   **Expected:** the default destination is correct; console multihoming has not
   been mistaken for replication of inventory/events or network logs.
5. Generate a harmless stdout/stderr marker and inspect current inventory/events.
   **Expected:** marker, source cluster/node/container identity, and fresh
   operational tables at the new LAW.

### Managed Prometheus

1. If a validated secondary AMW already receives the required samples, switch the
   query/Grafana source and approved rule path to it.
   **Expected:** usable secondary data without changing the cluster.
2. If collection is single-target and needs retargeting, use the validated
   [Kubernetes collection configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/kubernetes-data-collection-configure).
   For multiple destinations, follow the [multi-workspace pattern](https://learn.microsoft.com/en-us/azure/azure-monitor/containers/prometheus-metrics-multiple-workspaces)
   and verify scrape labels/DCR filters for each intended series. That example
   partitions data; merely adding a DCR is not proof of duplication.
   A customer-managed [direct remote-write sender](https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/prometheus-remote-write)
   instead needs each destination endpoint and DCR-scoped publisher permission.
   **Expected:** actual alternate ingestion for every critical target; any
   unvalidated fan-out or buffering behavior remains an explicit gap.
3. Check `up` and a known workload/network metric in the alternate AMW, with the
   expected cluster alias and labels. Compare sample timestamps over several
   configured scrape intervals.
   **Expected:** fresh samples, not cached dashboard history.
4. Activate secondary Prometheus rules and test notification delivery. Query one
   authoritative copy for fleet totals.
   **Expected:** no duplicated counts or paging when both workspaces receive
   the same series. Precreate secondary rule groups: the documented existing
   group's AMW selection cannot be changed. A single PromQL query cannot span
   AMWs; test separate Grafana data sources/queries.

### Advanced Container Networking and remaining paths

1. If standard cluster metrics arrive but ACNS metrics do not, inspect ACNS
   feature/agent health and its specific scrape configuration.
   **Expected:** the missing network collection is repaired without recreating
   all monitoring.
2. Treat flow logs/flow capture independently: inspect the actual flow exporter,
   output, filters, buffers, authentication and storage/LAW availability.
   For Azure Monitor forwarding, **manually update the associated DCR** when
   switching LAW as required by the stored-log setup warning; changing the
   workspace selection alone can leave logs stopped.
   **Expected:** fresh flow evidence at the configured destination; a recovered
   Prometheus chart does not count as flow-log recovery.
3. For control-plane diagnostic logs, use [LAW recovery](log-analytics-recovery.md).
   For application OTel, use [Application Insights recovery](application-insights-retargeting.md).
   **Expected:** each independent telemetry path has separate verification.

## Verification

- Check logs and metric freshness separately for **every surviving cluster**.
- Validate known marker logs, inventory/events, `up`, application series, and ACNS
  series. Test flow evidence only where its collection is actually enabled.
- Verify both source region/cluster identity and destination. Ensure aliases
  cannot merge two clusters into one apparently healthy series.
- Check agent/export errors, scrape cardinality, ingestion load and any backlog.
- Validate Grafana access, secondary rule execution and an actual notification.
- Record missing samples/logs; monitoring changes do not recover history that
  was never collected or transmitted.
- For ACNS, record the 50 MB/node rotation exposure, the setup guide's exclusion
  of logs older than two minutes at later onboarding, and any aggregation/
  throttling. Do not promise lossless replay of the outage.

## Rollback

Restore saved collection and destination configurations through the same supported
workflow, one cluster at a time. Verify primary freshness before changing query/
rule authority back. Keep the alternate workspace/history for reconciliation.
Remove temporary duplicate collection only after verifying that its association
is not shared with another signal or cluster. Do not delete a shared DCR/DCE
based only on its name. Record the final configuration so later changes preserve it.

## Escalation

Escalate to Kubernetes platform on-call for unhealthy agents, to Azure support for
unsupported collection patterns or persistent ingestion failures, and to the
incident commander for inadequate alternate capacity or exceeded recovery
windows. Stop if a repair would interrupt live calls or weaken network controls
without approval.

## Evidence capture

Use the [evidence template](../../evidence/templates/evidence-record.template.md):
record add-on versions, actual DCR/DCE locations, source/target map, changed
collection options, sample/log timestamps, markers, missing intervals,
cardinality/cost changes, alert results, and rollback. Mark unconfigured flow
logs and untested clusters as **gap**, not recovered.

## Related ADRs

- [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md):
  proposed telemetry resilience.
- [ADR-0010](../../adr/0010-active-active-multi-cluster-topology.md):
  independent workload regions; no mid-call migration.
