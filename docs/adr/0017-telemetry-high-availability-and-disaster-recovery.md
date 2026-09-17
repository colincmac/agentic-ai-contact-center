# ADR-0017: Regional monitoring with selective protection for critical data

- **ID:** ADR-0017
- **Status:** proposed
- **Date:** 2026-09-14
- **Last updated:** 2026-09-17

## Context

A multiregion solution should remain observable when a region fails. However,
keeping healthy regions monitored and preserving the failed region's historical
data are different requirements.

Azure monitoring also has several independent paths: resource diagnostic logs,
Container Insights, managed Prometheus, Application Insights, and Power Platform
exports. No single replication setting protects them all.

This ADR proposes an adaptable monitoring pattern for the solution accelerator.
It complements [ADR-0010](0010-active-active-multi-cluster-topology.md)'s workload
topology without prescribing the same recovery targets for every adopter.

## Decision drivers

- Keep healthy regions observable during another region's outage.
- Preserve critical history without duplicating all telemetry.
- Make recovery actions and ownership clear.
- Balance query availability, recovery time, cost and operational effort.
- Apply appropriate access, retention and data-residency controls to every copy.

## Considered alternatives

| Option | Why choose it? | Why not use it everywhere? |
| --- | --- | --- |
| **Shared monitoring destinations** | Simple configuration and queries | Shared regional dependencies |
| **Native Log Analytics replication** | One logical workspace with a managed secondary copy | Customer-initiated switchover, extra charges, and service/region restrictions |
| **Dual ingestion into independent workspaces** | Two immediately queryable copies and independent retention | Duplicate ingestion and more query/alert administration |
| **Primary plus archive** | Potentially lower-cost historical recovery | Slower access; archive recovery or replay is required |
| **Regional collection with selective protection** | Isolates regions and spends redundancy budget on valuable data | Some history remains unavailable unless explicitly protected |

See the [strategy comparison](../monitoring/high-availability.md#strategy-options)
for configuration and recovery trade-offs.

## Decision

**Recommend regional collection as the baseline, with additional protection
chosen separately for each telemetry path.**

1. **Use regional destinations.** Send regional logs to a Log Analytics workspace
   (LAW), application telemetry to Application Insights backed by a LAW in the
   same region, and managed Prometheus metrics to an Azure Monitor workspace
   (AMW). This isolates failures; it does not create a second historical copy.
2. **Protect critical history according to how quickly it is needed.** Use an
   independently recoverable archive when delayed access is acceptable. Use
   supported dual ingestion when logs must be queryable during an incident.
   Keep verbose, lower-value data single-copy unless requirements justify more.
3. **Choose global-service destinations independently of traffic routing.**
   Front Door, ACS, Event Grid system topics and multiregion Cosmos DB accounts
   retain their configured logging destinations when workload traffic moves.
   Designate a primary and add archive or queryable protection as needed.
4. **Use native LAW replication selectively, not as universal monitoring DR.**
   Evaluate it for supported logs where one logical workspace is valuable.
   Its reviewed support matrix excludes Application Insights over LAW and
   Container Insights; it does not recover AMW, Grafana or alert-rule resources.
5. **Prepare source-specific recovery.** Validate AKS console-log duplication and
   Prometheus collection separately. Prepare alternate Application Insights
   resources with independent LAWs. App Configuration can distribute a new
   destination, but exporters and APIM loggers must actually apply it.
6. **Keep Power Platform/Dynamics export administrator-owned by default.**
   Account for one Customer Service export per environment, local authentication,
   and the documented 24-hour delivery SLA. Use unattended retargeting only after
   confirming a supported mechanism.
7. **Recover access and notifications too.** Prepare secondary queries,
   dashboards, identities and alert rules, with one preferred copy for counts
   and paging.

Each adopter selects acceptable data loss, required historical coverage and
recovery time. Use the [configuration guide](../monitoring/high-availability.md#source-and-target-matrix)
to apply this decision to individual services.

## Consequences

**Benefits**

- Healthy regions are not dependent on a single monitoring region.
- Redundancy cost follows the value of the data rather than total volume.
- Source-specific procedures make manual and automated actions explicit.

**Trade-offs**

- Regional single-copy data can be inaccessible during a regional outage.
- Archives take longer to use than an already-queryable secondary workspace.
- Multiple workspaces require additional access, query and alert configuration.
- Destination changes can split a transaction's history; correlation identifiers
  and change timestamps are needed to investigate it.
- Shorter secondary retention reduces eligible storage charges, not duplicate
  ingestion charges.

### Operational implications

Adapt the [monitoring recovery runbook](../runbooks/monitoring/high-availability.md)
and its service-specific procedures to your environment. Assign owners, prepare
alternate destinations, and test ingestion, queries, notifications and failback.
Do not infer automatic telemetry recovery from workload failover.

### Security implications

Every additional destination is another copy of potentially sensitive data.
Apply least privilege, approved geography and retention to each copy; avoid
logging credentials, audio, authorization headers or unnecessary personal data.

The documented Power Platform export requires local authentication. Prefer a
separately governed destination for it rather than weakening Entra-only
application ingestion. Connection-string routing and authentication are separate
configuration decisions.

## Evidence

The [Microsoft source register](../monitoring/high-availability.md#microsoft-source-register)
supports the capability and limitation statements. This is a proposed reference
pattern, not measured HA/DR evidence. Cost examples are **modeled**; recovery
targets must be selected and tested for each adopting solution.

Record recovery exercise results with the
[evidence template](../evidence/templates/evidence-record.template.md).

## Revisit triggers

- Service support, supported regions or export-management APIs change.
- Recovery exercises miss the selected data-loss or recovery-time targets.
- Duplicate ingestion or metric volume exceeds the budget.
- Residency, retention or audit requirements change.
- Changes to APIM, collectors, exporters or networking invalidate a recovery path.
