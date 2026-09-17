# High availability and disaster recovery for monitoring

Use this guide to choose how your Azure solution collects, stores, and accesses
logs and metrics during an outage. This monitoring data, together with traces,
is called **telemetry**. The examples use a contact center, but the patterns also
apply to other multiregion solutions.

**Recommended starting point:** collect telemetry in each active region, then
add a second copy only where the value of the data justifies the cost. Choose
an archive when recovery can wait, or a second queryable workspace when you
need the data during an incident.

- **Decision:** [ADR-0017](../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed)
- **Recovery procedures:** [monitoring recovery runbook](../runbooks/monitoring/high-availability.md)
- **Microsoft sources reviewed:** 2026-09-14

This is design guidance, not a recovery-time guarantee. Check service support
for your regions and test the chosen configuration before relying on it.

## Start with three questions

1. **Must surviving regions keep collecting telemetry?** Use regional
   destinations so a monitoring outage in one region does not affect them all.
2. **Must you read the failed region's earlier logs during the outage?** Keep
   another queryable copy. Regional collection alone does not provide this.
3. **Can historical investigation wait?** An independently recoverable archive
   can cost less than keeping a second copy ready for queries.

These choices can differ by data type. For example, retain a second queryable
copy of critical delivery-failure logs, but archive verbose access logs.

## Understand the monitoring services

There is no single failover switch for Azure monitoring.

| Service or setting | What it does | What its recovery does not cover |
| --- | --- | --- |
| **Log Analytics workspace (LAW)** | Stores and queries logs, including Application Insights data | Managed Prometheus metrics, Grafana, or every service that sends it data |
| **Application Insights** | Receives application telemetry and uses a backing LAW | An independent region just because its backing workspace changes |
| **Azure Monitor workspace (AMW)** | Stores managed Prometheus metrics | Log Analytics data |
| **Diagnostic settings** | Route a resource's supported logs and metrics to destinations | Container agents, application exporters, or SaaS export configuration |
| **Grafana and alert rules** | Query data, visualize it, and notify operators | Durability of the underlying telemetry |

For AKS, a **data collection rule (DCR)** selects data and its destination; a
**data collection endpoint (DCE)** provides endpoints used by collection.
Changing a diagnostic setting does not change these separate collection paths.

**High availability (HA)** reduces interruptions, such as through supported
availability-zone redundancy. **Disaster recovery (DR)** restores service after
a larger failure, such as loss of a region. Zone resilience is not regional DR.

## Strategy options

Choose a baseline, then add protection for selected data. These options are
complementary, not a requirement to deploy every mechanism.

| Option | Best fit | Main benefit | Main trade-off |
| --- | --- | --- | --- |
| **One shared destination per data type** | Simple solutions that can accept a regional monitoring outage | Fewer resources and simpler queries | A shared regional dependency; use LAW for logs and AMW for Prometheus |
| **Regional collection** | Multiregion solutions | Surviving regions keep their own monitoring | No second copy of the failed region's history |
| **Primary plus archive** | Historical data that can tolerate slower recovery | Potentially lower storage cost | Requires archive access or replay; not immediately queryable in LAW |
| **Two independent workspaces** | Critical data needed during an incident | Both copies are queryable, with independent retention | Duplicate ingestion and more query/alert configuration |
| **Native LAW replication** | Supported logs that should retain one logical workspace identity | Azure maintains a secondary copy | Customer-initiated switchover, replication charges, and service/region restrictions |
| **Prepared alternate destination** | Future collection matters more than a second historical copy | Little duplicate ingestion before an outage | Configuration must change; history and cutover gaps may remain |

### What happens automatically?

| Mechanism | Automatic behavior after configuration | Action you still own |
| --- | --- | --- |
| Supported zone redundancy | Azure handles supported in-region failures | Confirm zone support; plan separately for regional loss |
| Regional collection | Healthy regions continue on their existing paths | Recover the affected region or change its destination |
| Dual ingestion | Both destinations receive the selected data | Select a healthy query/alert path and avoid duplicate counts |
| Native LAW replication | Azure replicates new data | Decide when to switch over and switch back |
| Archive | Configured export and storage redundancy operate | Recover access, query or replay data, and account for missing records |
| Alternate Application Insights destination | SDK retries may bridge a short interruption | Change the destination and ensure the sender actually uses it |

Automating a customer action does not make it a built-in service failover.
Also, two destinations still share the same producer: neither can recover an
event that was never generated or sent.

### Native LAW replication: when to use it

Consider [workspace replication][S1] when you need supported logs in another
region while retaining one logical workspace. Check these limits first:

- The secondary is a hidden copy, not an independently managed LAW. You cannot
  give it a separate, shorter retention policy.
- Only new data is replicated after enablement; older history is not copied.
- Switchover and switchback are customer-initiated. Replication is asynchronous,
  and DNS/client changes take time.
- The reviewed support table excludes **Application Insights over LAW,
  Container Insights, VM Insights, and Auxiliary tables**. Do not treat backing
  workspace replication as end-to-end recovery for those experiences.
- Only listed region combinations are supported. Applicable DCRs must use the
  workspace's system DCE and target only that workspace.
- Workspace management changes are restricted during switchover. Alert-rule
  resources are not automatically replicated.

Price replication for the whole selected workspace. Misconfigured DCRs can
leave data unprotected while still incurring charges; omitting required
associations is not a cost-saving strategy. See [Logs reliability][S2] and the
[LAW recovery procedure](../runbooks/monitoring/log-analytics-recovery.md).

### Archive versus a second workspace

An **archive** is for recoverable history; a **second LAW** is for ready-to-query
history. Do not assume either automatically provides the other.

Direct diagnostic export can send supported logs to Storage or Event Hubs.
Alternatively, [LAW data export][S5] exports selected tables as new data arrives.
The latter depends on the LAW ingestion/export path and does not copy old history.

For an archive to survive a regional outage, choose suitable storage redundancy
and test access from another region. Zone-redundant storage alone does not protect
against region loss. Event Hubs metadata DR is not message-data replication.
Include query/replay time, bounded retries, possible duplicates, and recovery
costs in the design.

## Example regional layout

Use regions A and B as a starting pattern, not prescribed Azure locations:

| Telemetry | Region A | Region B |
| --- | --- | --- |
| Resource and container logs | LAW-A | LAW-B |
| Application telemetry | Application Insights-A, backed by LAW-A | Application Insights-B, backed by LAW-B |
| Managed Prometheus | AMW-A | AMW-B |
| Queries and dashboards | Access to the required regional data | Alternate access with its own tested permissions |

Keep each Application Insights resource and its LAW in the **same region**.
Two Application Insights resources sharing one LAW still share a storage/query
dependency. [S26]

For global services, designate a primary log destination and protect selected
history with an independent archive or second LAW. For Dynamics/Power Platform,
consider a dedicated environment-specific Application Insights resource because
its authentication and administration requirements differ.

## Source and target matrix

### Regional versus global resources

**Regional resources:** send to their regional monitoring destination. Configure
each Redis instance, AKS cluster, or separate APIM service independently.

**Global or multiregion resources:** choose destinations for resilience and data
residency, not for the current location of application traffic. A Front Door
origin change or Cosmos DB region failover does not select a different LAW.
Configure the shared resource's log settings accordingly.

For both, use an archive if recovery can wait, or dual ingestion if critical
history must be queryable during the outage.

### Resource diagnostic settings

The same destination rules apply across these services; the resource scope and
log categories differ.

| Source | Where to configure | Important distinction |
| --- | --- | --- |
| **Front Door Standard/Premium** | Global profile: access, WAF and health-probe logs | Logging destinations do not follow origin failover. Preserve `X-Azure-Ref` for investigation. [S7] |
| **Cosmos DB** | Account: API-appropriate request, control and query categories | One account may cover several regions. Prefer resource-specific tables where supported; verify metric export support separately. [S8] |
| **Azure Managed Redis** | Each regional database for connection events | `ConnectionEvents` records connections/authentication, not commands or call-state history. Cache geo-replication does not copy log settings. [S9] |
| **AKS control plane** | Each cluster's diagnostic settings | API/audit/control-plane logs are separate from container stdout, Prometheus and application traces. |
| **Azure API Management (APIM) resource logs** | Each APIM service's diagnostic settings | This is a different path from the Application Insights logger described below. |
| **Azure Communication Services (ACS)** | Each ACS resource: Call Automation operational and media-related categories | Global resource location does not remove data-geography requirements. Logs are not raw audio. [S10] |
| **Event Grid** | The actual system topic, topic, domain or namespace | ACS system topics expose `DeliveryFailures`; do not assume custom-topic categories also apply. Dead-lettered events are not diagnostic-log backups. [S11] |
| **App Configuration** | Each store; verify coverage of its replicas | Audit logs and aggregated HTTP request logs differ. Configuration replication does not choose a new logging target. [S12] |
| **Key Vault, Storage and Azure Container Registry** | Vault/registry and the relevant Storage service scopes | Secret, object or image redundancy does not provide LAW redundancy. |
| **Other Azure services** | The actual resource type and supported categories | Check Foundry/AI Services, Speech, Search, Container Apps and network resources individually. SDK telemetry is a separate path. [S13] |
| **Activity Log and Service Health** | Subscription-level export and health notifications | Resource diagnostic settings alone do not cover subscription-level changes. |

**Configuration rules to remember** [S3]:

- A resource supports up to **five diagnostic settings**, with at most one
  destination of each type per setting. Two LAWs require two settings.
- For regional sources, diagnostic destinations in Storage/Event Hubs must be
  in the source region. LAW does not have the same restriction. Verify provider
  rules and residency requirements for global sources.
- Category selection is not a row filter. Filtering within a category requires
  a supported transformation or a separate collection design.
- Keep native platform metrics in Azure Monitor Metrics unless you need an
  exported copy. Diagnostic export supports only eligible metrics and flattens
  dimensions; `AllMetrics` is not a backup of Prometheus.
- Test destination permissions, firewalls and private connectivity in advance.

Recovery: [LAW and diagnostic settings](../runbooks/monitoring/log-analytics-recovery.md).

### AKS non-diagnostic-setting logs and metrics

Choose protection separately for these three paths:

| Path | Starting configuration | Option for additional protection |
| --- | --- | --- |
| **Container Insights logs** | Agent and collection rules to regional LAW | Supported namespace-based duplication of stdout/stderr |
| **Managed Prometheus metrics** | Scrape configuration and DCR/DCE to regional AMW | Validated multi-destination collection or remote write |
| **Advanced Container Networking Services (ACNS)** | Network metrics through Prometheus; stored flow logs through their log collector | Protect the metric and log destinations separately |

**Container logs.** [Multihoming][S15] means sending the same namespace's
stdout/stderr to multiple LAWs. It requires high-scale mode and separate
`ContainerLogV2Extension` rules. It does **not** automatically duplicate inventory,
Kubernetes events or network flow logs. Check the selected regional combination,
[high-scale prerequisites][S21] and [region mapping][S22] before adopting it.
The default Kubernetes collection rule still has its own destination limits.
[S14]

**Prometheus.** The documented [multiple-workspace pattern][S16] routes selected
metrics to different AMWs; adding a rule is not proof that every series is
duplicated. [Remote write][S17] is another option for customer-managed senders,
with its own identity, buffering and operational requirements. Neither should
be presented as transparent regional failover without validating that design.

A single PromQL query cannot span AMWs. Prepare separate data sources and
[secondary rule groups][S19]; changing ingestion does not move existing rules.
AMW has 18-month retention rather than a configurable short-retention standby,
and deletion has no soft-delete recovery. [S18]

**Network telemetry.** ACNS metrics use the Prometheus path [S25]. Stored flow
logs require supported cluster settings and a `ContainerNetworkLog` resource;
ACNS enablement alone is not enough. Logs can be forwarded to `ContainerNetworkLogs`
in LAW. Changing that LAW can require a **manual DCR update**. Node-local files
are bounded, and later forwarding does not recover all earlier data. [S20], [S24]

Recovery and detailed checks: [AKS telemetry runbook](../runbooks/monitoring/aks-telemetry-recovery.md).

### Application Insights, APIM and SaaS export

#### Application Insights: distinguish three choices

| Choice | What changes | What does not change |
| --- | --- | --- |
| **Another Application Insights resource with an independent LAW** | Future telemetry can use a separate regional ingestion/storage path | Earlier history stays in the original destination |
| **Change the existing resource's associated LAW** | Its backing-workspace association | The Application Insights resource's region; this is not full regional failover |
| **Export telemetry to another LAW or Storage** | A downstream copy of supported data | Dependence on the original Application Insights ingestion/export path |

An existing Application Insights resource cannot simply be moved to another
region with its history. Prepare the new resource, permissions, connection
string, queries and alerts. Supported SDK retries help with temporary outages
but are not a durable regional backup. [S27], [S34], [S35]

For diagnostic export to another LAW, the target must differ from the backing
LAW. Follow Microsoft's access guidance to avoid duplicate transactions and
application maps when both copies are visible. [S3]

#### APIM: regional loggers, then an alternate if needed

For independently deployed APIM services, use a regional Application Insights
resource and same-region LAW for each service.

| Situation | Action |
| --- | --- |
| APIM-A fails and requests move to healthy APIM-B | Leave APIM-B's healthy logger unchanged |
| APIM-A is healthy, but its telemetry destination fails | Switch its diagnostics to a prepared alternate logger |
| One APIM service has gateways in several regions | Account for shared logger/diagnostic scopes; a change can affect all gateways using them |

Create primary and alternate loggers in advance. Prefer connection string plus
managed identity, with Monitoring Metrics Publisher on each target Application
Insights resource. Use the [supported configuration procedure][S28]; the portal's
logger-creation flow and APIM workspace integration have different authentication
capabilities.

To switch, update the effective diagnostic **`loggerId`**. Check **All APIs**
and every overriding API/version. Changing a shared logger's credentials instead
affects all diagnostics referencing it. Preserve sampling, correlation and
payload settings, then verify fresh telemetry through each affected gateway.

Two loggers do not enable automatic failover or dual logging. Per-API settings
normally override All APIs; multiplexing to different loggers requires Microsoft
Support enablement. APIM resource diagnostic settings remain a separate LAW path.
[S28]

Recovery: [Application Insights and APIM](../runbooks/monitoring/application-insights-retargeting.md).

#### Dynamics and Power Platform: a separate export configuration

Configure conversation diagnostics in **Power Platform admin center > Manage >
Data export > App Insights**, selecting **Dynamics Customer Service**, the
environment, and the destination. There is **one Customer Service export per
environment**. Dataverse diagnostics/performance and other product exports are
separate export types, not additional copies of that conversation stream.
[S29], [S30], [S31]

Plan for these differences [S32]:

- Managed Environment, licensing, Azure destination permissions, and the required
  tenant/environment administrator roles must be in place.
- The documented integration requires **local authentication**. A dedicated
  environment-specific Application Insights resource can isolate this requirement
  from application telemetry using Entra-only ingestion.
- Initial export can take up to **24 hours**, and the guide states a **24-hour
  delivery SLA**. This is not a rapid-failover guarantee.
- The documented administration path is create/delete and reconnect. Use a
  verified administrator-led replacement procedure as the baseline; unattended
  retargeting needs a confirmed supported API and a rehearsal.

Changing an App Configuration key does not change this export. Retargeting does
not move history or guarantee replay. Verify cloud availability, private-network
compatibility and the exact export type. The Dynamics Diagnose dashboard also
has its own delay and excludes transfer/consult diagnostics. [S36]

Recovery: [Power Platform export runbook](../runbooks/monitoring/power-platform-telemetry-recovery.md).

#### Other telemetry sources

Browser telemetry, Foundry tracing, custom tools and other exporters each need
their own destination inventory. Do not assume one connection-string change
updates them all.

Teams Phone, Call Quality Dashboard and Graph call records use reporting or
collection paths rather than native Application Insights spans. If you collect
them, plan checkpoint recovery, subscription renewal where applicable, and
deduplication separately.

## Automation design for telemetry destination changes

**App Configuration can distribute a new destination; it does not perform the
whole failover.** An exporter is the sender that transmits telemetry. It must
refresh its configuration and actually start using the new target.

| Step | What to automate or verify |
| --- | --- |
| **Prepare** | Store approved primary/alternate destinations by environment and region. Test authentication, capacity and network access to both. |
| **Detect** | Confirm sustained ingestion/query failure using an independent signal, not just missing traffic or a blank dashboard. |
| **Switch a small group** | Save the previous settings, update a versioned configuration, and refresh or safely restart the affected exporters. |
| **Verify and expand** | Confirm fresh test data and alert delivery at the alternate before changing more senders. Stop on errors. |
| **Return deliberately** | Wait for sustained primary health, restore a small group, and verify again. Avoid repeated automatic switching. |

App Configuration geo-replication helps keep configuration available and its
providers can fail over reads. It does not detect an Application Insights outage
or choose another component. Replicas synchronize eventually; separate stores
need explicit updates. [S33]

Use the **complete alternate connection string**, not a modified hostname.
Some exporters read settings only at startup. Confirm refresh behavior for your
SDK/collector and avoid interrupting active sessions just to redirect telemetry.
APIM logger changes and Power Platform exports still use their own configuration
surfaces.

Start with operator-approved automation. Before making it unattended, add a
single change owner, version/conflict checks, bounded retries, an abort path and
a working rollback. Keep the automation and its credentials reachable outside
the failed region. Do not introduce an APIM/Front Door ingestion proxy as an
assumed failover mechanism; that is a separate design requiring validation.

## Query, alert, and dashboard resilience

Restoring ingestion is only part of recovery:

- Prepare alternate Grafana/direct-query access, data sources, permissions and
  private connectivity. Grafana does not store the underlying telemetry.
- Configure secondary log-search and Prometheus rules, action groups and
  notification ownership. Test actual notifications, not just rule existence.
- Query one preferred copy for totals; otherwise dual ingestion can double
  counts and pages. For reconciliation, use source event/span identifiers, not
  just a call ID shared by many events.
- Retain region and correlation identifiers so a transaction spanning a
  destination change can still be investigated. See the
  [correlation guidance](correlation-model.md).
- Report inaccessible sources and missing intervals explicitly. A query over
  healthy workspaces is not proof that all regions are healthy.

Managed Grafana offers supported, billable zone redundancy on the Standard tier
at creation time. Azure handles that in-region recovery; separate regional
Grafana workspaces, configuration synchronization and user routing remain your
responsibility. [S23]

## Costs: what actually saves money

**Reduce duplicate volume before reducing retention.** Ingestion is charged for
each independent copy. Shortening a secondary LAW's retention does not remove
that ingestion charge.

| Cost choice | Benefit | Trade-off |
| --- | --- | --- |
| Duplicate only selected categories/namespaces/series | Less second-copy ingestion | Other data may be unavailable during an outage |
| Shorter retention in an independent secondary LAW | Lower eligible retention charges | Less recovery history |
| Archive instead of a second queryable LAW | Potentially cheaper historical storage | Slower access; export, query or replay charges |
| Sampling and collection filters | Lower telemetry volume | Reduced investigation detail; unsuitable for records that must be complete |
| Fewer Prometheus labels/series and longer scrape intervals | Lower sample volume | Less granular metric analysis |
| Regional workspaces | Better failure isolation | More administration and potentially smaller commitment-tier discounts |

### Example: selective duplication

**Modeled volume, not a price quote:** suppose you collect 100 GB/day and need
10 GB/day of critical logs in another LAW.

- Duplicate everything: **200 GB/day** ingested across both workspaces.
- Duplicate only critical logs: **110 GB/day**.

That saves 90 GB/day of duplicate ingestion. Actual cost depends on region,
pricing plan, commitment and other charges.

### Primary long retention, secondary short retention

This works with **independent LAWs**, not the hidden secondary of native LAW
replication. For a 10 GB/day stream, reducing total retention from 180 to 30 days
reduces the eventual retained footprint by roughly 1,500 GB. Savings depend on
the included retention allowance and storage tier. Reducing analytics retention
below its included 31 days does not reduce its cost; some tables have different
allowances. [S4], [S6]

Also review unnecessary `AllMetrics` export, broad `allLogs` selection, APIM
payload capture, transformation charges, export costs and secondary dashboards.
Use budgets and volume alerts rather than daily caps as routine cost control:
a cap can stop evidence collection during an incident.

## Recovery objectives and acceptance gates

Before adopting a pattern, record a small decision sheet for each telemetry path:

| Question | Record for your solution |
| --- | --- |
| What needs protection? | Source, categories/tables or metric series, and owner |
| How much loss is acceptable? | Recovery point objective (RPO): acceptable missing data, plus required historical coverage |
| How quickly must monitoring return? | Recovery time objective (RTO): time to fresh queries and working notifications |
| Which option meets that need? | Primary, alternate/archive, regions, retention and estimated cost |
| Who changes what? | Supported configuration surface, permissions, manual/automated steps and rollback |
| How will you prove it works? | Harmless test event, expected arrival time, alert test and recovery exercise |

Test source failure, ingestion failure, query-only failure and lost configuration
access separately. Include failback, missing history and duplicate records.
Buffering is bounded; no pattern here promises zero loss.

Use the [recovery runbooks](../runbooks/monitoring/high-availability.md) as a
starting point and record results with the
[evidence template](../evidence/templates/evidence-record.template.md).

## Microsoft source register

Sources reviewed on 2026-09-14; recheck service, region and pricing details when
adopting the guidance.

- **Log Analytics:** [replication][S1], [reliability][S2],
  [diagnostic settings][S3], [costs][S4], [data export][S5], [retention][S6].
- **Resource logs:** [Front Door][S7], [Cosmos DB][S8], [Managed Redis][S9],
  [ACS][S10], [Event Grid system topics][S11], [App Configuration][S12],
  [supported logs by resource type][S13].
- **AKS:** [collection configuration][S14], [console-log multihoming][S15],
  [Prometheus routing][S16], [remote write][S17], [AMW][S18],
  [Prometheus rules][S19], [ACNS log setup][S20], [high-scale logs][S21],
  [region mapping][S22], [flow-log behavior][S24], [scrape targets][S25].
- **Application Insights and visualization:** [Grafana reliability][S23],
  [Well-Architected guidance][S26], [FAQ][S27], [APIM integration][S28],
  [resource configuration][S34], [OpenTelemetry configuration][S35].
- **SaaS and configuration:** [Dynamics conversation diagnostics][S29],
  [Power Platform conversation export][S30], [integration overview][S31],
  [export administration][S32], [App Configuration replication][S33],
  [Dynamics Diagnose dashboard][S36].

[S1]: https://learn.microsoft.com/en-us/azure/azure-monitor/logs/workspace-replication
[S2]: https://learn.microsoft.com/en-us/azure/reliability/reliability-monitor-logs
[S3]: https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings
[S4]: https://learn.microsoft.com/en-us/azure/azure-monitor/logs/cost-logs
[S5]: https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-data-export
[S6]: https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-retention-configure
[S7]: https://learn.microsoft.com/en-us/azure/frontdoor/monitor-front-door
[S8]: https://learn.microsoft.com/en-us/azure/cosmos-db/monitor-resource-logs
[S9]: https://learn.microsoft.com/en-us/azure/redis/monitor-diagnostic-settings
[S10]: https://learn.microsoft.com/en-us/azure/communication-services/concepts/analytics/logs/call-automation-logs
[S11]: https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-logs/microsoft-eventgrid-systemtopics-logs
[S12]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/monitor-app-configuration
[S13]: https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-logs/logs-index
[S14]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/kubernetes-data-collection-configure
[S15]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-multitenant
[S16]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/prometheus-metrics-multiple-workspaces
[S17]: https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/prometheus-remote-write
[S18]: https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/azure-monitor-workspace-overview
[S19]: https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/prometheus-rule-groups
[S20]: https://learn.microsoft.com/en-us/azure/aks/how-to-configure-container-network-logs
[S21]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-high-scale
[S22]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/container-insights-region-mapping
[S23]: https://learn.microsoft.com/en-us/azure/reliability/reliability-managed-grafana
[S24]: https://learn.microsoft.com/en-us/azure/aks/container-network-observability-logs
[S25]: https://learn.microsoft.com/en-us/azure/azure-monitor/containers/prometheus-metrics-scrape-default
[S26]: https://learn.microsoft.com/en-us/azure/well-architected/service-guides/application-insights
[S27]: https://learn.microsoft.com/en-us/azure/azure-monitor/app/application-insights-faq
[S28]: https://learn.microsoft.com/en-us/azure/api-management/api-management-howto-app-insights
[S29]: https://learn.microsoft.com/en-us/dynamics365/customer-service/administer/configure-conversation-diagnostics
[S30]: https://learn.microsoft.com/en-us/power-platform/admin/conversation-diagnostics-application-insights
[S31]: https://learn.microsoft.com/en-us/power-platform/admin/overview-integration-application-insights
[S32]: https://learn.microsoft.com/en-us/power-platform/admin/set-up-export-application-insights
[S33]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-geo-replication
[S34]: https://learn.microsoft.com/en-us/azure/azure-monitor/app/create-workspace-resource
[S35]: https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration
[S36]: https://learn.microsoft.com/en-us/dynamics365/contact-center/use/diagnose-dashboard
