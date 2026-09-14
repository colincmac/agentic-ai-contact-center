# ADR-0017: Source-specific telemetry high availability and disaster recovery

- **ID:** ADR-0017
- **Status:** proposed
- **Date:** 2026-09-14

## Context

The contact center needs observability during the same regional incidents that affect its call-processing infrastructure. Workload failover does not imply logging failover. Diagnostic settings, Container Insights, managed Prometheus, Application Insights, and Power Platform exports have different ingestion paths.

[ADR-0010](0010-active-active-multi-cluster-topology.md) remains the accepted workload topology decision. This proposal focuses specifically on telemetry-specific failure boundaries.

## Decision drivers

- Keep surviving regions observable without depending exclusively on a failed region's ingestion, query, configuration, or dashboard service.
- Distinguish availability of future telemetry from recovery of historical data.
- Preserve canonical call identifiers while avoiding double-counting copies.
- Spend on redundancy according to the operational value of each stream, rather than duplicating every debug record and metric.
- Use supported recovery mechanisms, with explicit human ownership and tested automation where feasible.
- Respect geography, privacy, access, and retention requirements for every copy.
- Establish measured recovery objectives before production approval. 

**NOTE**: Currently, there is no validated telemetry RPO or RTO.

## Considered alternatives

1. **Single shared sink with in-region resilience**, i.e. send everything to one LAW. Lowest topology complexity and convenient joins. Credible for development or a workload that explicitly accepts region-wide loss of observability. Not sufficient for regional DR.
2. **Native [Log Analytics Workspace (LAW) replication](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/workspace-replication) everywhere.** LAW has built in regional-replication capabilities.  
   * Preserves one logical workspace identity for supported streams. 
   * Replication is chargeable & duplication can be expensive.
   * Switchover is customer-initiated, not automatic. Any ingestion stream that uses Data Collection Rules needs to be adjusted.
   * The current support table excludes Application Insights over LAW and Container Insights. 
   * It does not replicate managed Prometheus or Grafana.
   * Overall limited regional availability & limited choice of target replication regions
3. **Full dual ingestion into independent regional sinks**, i.e. send everything to 2 locations when possible. Gives two queryable copies where the source supports it, with independently configured retention. 
   * Rejected overall as the default because duplicate ingestion, metric samples, queries, dashboards, alerting, and operations all add cost. 
   * Retained for justified critical streams.
4. **Regional single-copy collection plus selective redundancy.** Preferred baseline below. Surviving regions remain observable, while selected global and critical streams receive additional protection. Unreplicated history from a failed region can remain unavailable.
5. **Archive or queue-mediated recovery.** Useful for audit retention or replayable custom pipelines. Storage and Event Hubs are not substitutes for an immediately queryable LAW/AMW, and a pipeline downstream of an unavailable ingestion service cannot recover data that never reached it.

The detailed [option comparison](../monitoring/high-availability.md#strategy-options) records applicability, cost, and recovery trade-offs.

## Decision

**Tiered, source-specific design.**

1. Use a regional LAW and AMW for each active application region, and a regional Application Insights component backed by that region's LAW for application telemetry. Regional single-copy collection is failure isolation, not a second copy of each region's history.
2. Treat global or account-wide sources as independent of application traffic placement. For Front Door, ACS, Event Grid system topics, and a multiregion Cosmos DB account, select diagnostic destinations at the actual ARM resource scope. Send approved critical categories to two independent LAWs, while keeping high-volume investigation-only categories single-copy unless justified.
3. Use source-specific AKS collection: evaluate documented namespace-based console-log multihoming with high-scale mode, without claiming it duplicates all Container Insights/ACNS streams. Validate regional support and any Prometheus duplicate-delivery/remote-write design before treating them as DR. Do not infer support from a general-purpose AMA multi-destination DCR recipe or assume native AMW regional failover was established by this research.
4. Preprovision alternate Application Insights components **and their independent backing workspaces**. Automate application and APIM destination changes only through supported configuration surfaces, with canary verification and controlled rollout. An App Configuration value change is not proof that an exporter has changed its active destination.
5. Keep Dynamics/Power Platform export destinations and recovery ownership explicit. Use the documented administration flow as the recovery baseline; unattended retargeting requires a verified supported API and tenant rehearsal. Account for its local-authentication requirement, one Customer Service export per environment, and documented 24-hour delivery SLA. Do not promise SaaS export replay or live detection latency.
6. Maintain secondary query/dashboard access, rules, permissions, private connectivity, and an independent incident signal. Successful data ingestion alone is not restored observability.
7. Require an approved source/target inventory, retention and redundancy tier, recovery objectives, and drill evidence before accepting this ADR.

The [per-source configuration matrix](../monitoring/high-availability.md#source-and-target-matrix) is the proposed routing contract.

## Consequences

- Most verbose telemetry is ingested once; critical data can have a higher resilience budget. Savings depend on actual volume and contract prices.
- Cross-workspace queries replace the convenience of assuming all call evidence lives in one workspace. During an outage, queries must declare missing sources rather than interpreting missing records as successful calls.
- Failover can split one call's telemetry between components/workspaces. Correlation identifiers and destination-change timestamps must survive.
- Independent sinks require configuration parity and independent access checks. Native replication reduces some configuration duplication, but introduces its own support and management restrictions.
- Not all producers can dual-write or switch without delay. The design explicitly accepts gaps only where the business has approved them.

### Operational implications

Use the draft runbooks for [incident routing](../runbooks/monitoring/high-availability.md),
[LAW and diagnostic recovery](../runbooks/monitoring/log-analytics-recovery.md),
[AKS telemetry recovery](../runbooks/monitoring/aks-telemetry-recovery.md),
[Application Insights retargeting](../runbooks/monitoring/application-insights-retargeting.md),
and [Power Platform export recovery](../runbooks/monitoring/power-platform-telemetry-recovery.md).
They are not production-certified procedures. Switchover/failback approvals,
on-call ownership, alert deduplication, and failback stability windows must be
recorded for each environment.

### Security implications

Each additional destination is another copy of potentially sensitive call
metadata. Minimize payload capture; do not replicate audio, credentials, OTPs,
authorization headers, or unredacted prompts as a side effect of this decision.
Apply least privilege, approved residency, retention/deletion policies, and
network controls to both destinations. Keep actual connection strings and
resource inventory out of public documentation. Connection-string routing and
ingestion authentication are separate concerns. The documented Power Platform
export requires local authentication; isolate that requirement in an approved
SaaS component rather than weakening Entra-only application ingestion globally.

## Evidence

This is a proposal.
Microsoft documentation and repository source inspection inform the
[research guide and source register](../monitoring/high-availability.md).
No Azure deployment, failover drill, supported-exporter hot-reload test,
Power Platform retargeting test, cost benchmark, or end-to-end telemetry recovery
measurement was performed for this change.

Illustrative cost arithmetic is explicitly **modeled**; proposed objectives are
**assumed** until approved; recovery outcomes are an evidence **gap**. Existing
[local runtime evidence](../evidence/2026-09-09-dotnet-runtime-validation.md)
does not validate telemetry HA/DR. Capture future drill results with the
[evidence template](../evidence/templates/evidence-record.template.md).

## Revisit triggers

- Microsoft changes the replication support matrix, supported region pairs, Container Insights collection model, or managed Prometheus resilience options.
- A supported Power Platform export-management API is verified for the required export type, permissions, environment, and recovery scenario.
- Measured recovery time or data loss exceeds an approved source-specific target.
- Duplicate ingestion, retention, or metric cardinality exceeds its budget.
- Compliance requires longer recovery history or prohibits a secondary geography.
- A change in APIM tier, cluster topology, telemetry SDK, or network architecture invalidates the tested routing/recovery path.
