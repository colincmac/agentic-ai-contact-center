# Logging and metrics high availability and disaster recovery

## Summary

**TERMS**

AI - Application Insights

LAW - Log Analytics Workspace

AMS - Azure Monitor Workspace (Prometheus/AKS)

1. **There is no single failover switch for each observability plane.** 
   * Log Analytics Workspace (LAW) stores logs
   * Azure Monitor workspace (AMW) stores managed Prometheus metrics
   * Application Insights adds its own ingestion/component boundary
   * Grafana and/or alert rules are additional dependencies
2. **Native LAW replication is customer-switched, not automatic regional failover.** It preserves a logical workspace identity but only copies new data after enablement. The current support table explicitly lists **LAW-backed Application Insights and Container Insights as not supported**. [S1], [S2]
3. **Two diagnostic settings can send to two independent LAWs.** They are concurrent destinations, not primary/standby routes. Resource/global service failover does not retarget them. Shorter secondary retention reduces eligible retention charges, **not duplicate ingestion charges**. [S3], [S4]
4. **Two Application Insights components backed by the same LAW still share a storage/query dependency.** For independent regional recovery, provision the alternate component and its own LAW, identities, connectivity, and queries. Changing a connection string changes future routing, not historical data.
5. **App Configuration is a distribution mechanism, not a failover engine.** Changing a key can be automated; provider refresh, SDK/exporter reinitialization, safe voice-pod rollout, and canary verification are separate engineering work. APIM and SaaS exports require their own configuration paths.
6. **Regional single-copy collection isolates failures but does not replicate history.** Use selective redundancy for high-value signals, and explicitly accept gaps for lower-value data. Do not duplicate every stream by default.

### Summary of Suggestions

- **Use regional observability resources**. For each active application region, use a regional Log Analytics workspace for operational/container logs, an Application Insights resource backed by that region’s workspace, and a separate Azure Monitor workspace for managed Prometheus. This keeps surviving regions observable, but does not copy the failed region’s historical data.
- **Protect critical history selectively**. For global-service Azure Diagnostic logs, use a designated primary LAW plus an independently recoverable archive for approved critical categories. Secondary archive can be another LAW, Azure Storage, etc. Where incident response requires immediately queryable history in either region, use supported dual ingestion into independent workspaces instead.
- **Use Azure App Configuration for App Insights connection strings (when possible)**. Automation must also refresh or restart the actual exporter safely and verify fresh telemetry at the alternate destination. Note APIM’s own logger requires a separate configuration change.
- **Treat Power Platform/Dynamics recovery as an administrator-owned procedure**. The documented baseline is one Customer Service export configuration per environment, with manual replacement to change the destination. This doesn't use App Configuration for the App Insights connection. It must have a manual update.
- **Recover metrics, dashboards, and alerts separately**. Log Analytics replication does not protect managed Prometheus. Any secondary metrics copy requires a validated collection design. Prepare secondary Grafana or direct-query access, data sources, identities, alert rules, and notification paths. Grafana provides visualization, not telemetry durability.

Example topology looks like this:
- `LAW-A` / `LAW-B`: regional operational and application logs.
- `AI-A -> LAW-A` / `AI-B -> LAW-B`: regional Application Insights components.
- `AMW-A` / `AMW-B`: regional managed Prometheus stores.
- CCaaS integration: A dedicated Application Insights component/workspace and a preprovisioned alternate where separate access, retention, or ownership is required.
- (Optional) Secondary Grafana/direct query access and alert definitions with tested RBAC.
- (Optional) Second independent LAW for **supported critical operational logs**, as an alternative to using LAW replication for those streams.


### Sections

- [Strategy comparison](#strategy-options)
- [Resource diagnostic targets](#source-and-target-matrix)
- [AKS logs, Prometheus, and network telemetry](#aks-non-diagnostic-setting-logs-and-metrics)
- [Application Insights, APIM, and SaaS export](#application-insights-apim-and-saas-export)
- [Automatic versus manual changes](#automation-design-for-telemetry-destination-changes)
- [Costs and retention](#cost-controls-and-the-retention-hypothesis)
- [Recovery objectives and validation](#recovery-objectives-and-acceptance-gates)

## Telemetry by Data Path

| **Telemetry class** | **Examples** | **Native destination** | **Recommended DR approach** |
| --- | --- | --- | --- |
| **Azure resource/platform logs** | Front Door, Cosmos DB, Redis, APIM, ACS, Event Grid, App Configuration, AKS control plane | Diagnostic settings to Log Analytics, Storage, or Event Hubs | Regional LAW plus independent archive for critical logs |
| **Container/Kubernetes logs** | stdout/stderr, Kubernetes events, inventory | Container Insights to Log Analytics | Regional LAW per cluster or regional stamp |
| **Prometheus metrics** | AKS, Advanced Container Networking, workload metrics | Azure Monitor workspace | Regional AMW, scrape each cluster locally |
| **Application telemetry** | AKS applications, APIM, services using OpenTelemetry | Workspace-based Application Insights | Regional App Insights per workload/environment |
| **Power Platform/Dynamics telemetry** | Dataverse, Power Automate, D365 Contact Center conversation diagnostics | Power Platform data export to Application Insights | Treat as a manually managed SaaS integration |

## Strategy options

| Strategy | Configuration | Automatic versus customer action | History and cost trade-off |
| --- | --- | --- | --- |
| Single LAW/AMW with in-region resilience | Select supported regions and zone-resilient service configuration | Azure handles supported in-region failover; region-wide recovery remains a different concern | One ingestion copy; accepts regional observability dependency |
| Native LAW replication | Enable supported secondary; attach applicable DCRs to system workspace DCE | Replication is automatic after setup; **switchover and switchback are customer-initiated** | New logs only; billable ingestion replication charge; no independently shorter-retention shadow; exclusions apply |
| Dual independent LAW ingestion | Two diagnostic settings or another explicitly supported source fan-out mechanism | Both ingest normally; operator/automation changes query/rule selection, not a native LAW failover | Independent history from enablement; both ingestion copies billed; retention can differ |
| Regional single-copy collection | Each region's producers use its local LAW/AI/AMW; federated operator view | Surviving regions continue on their existing paths; failed sink recovery/retargeting is source-specific | Cost-efficient isolation; failed-region history may be unavailable |
| Warm alternate destinations | Precreate alternate components/workspaces/configuration; route only on incident | Manual or custom approved automation; no universal Azure Monitor routing policy | Lower steady-state duplicate volume; little/no preincident alternate history; cutover/replay gaps |
| Archive or queue | Azure Diagnostic export to Storage/Event Hubs where supported | Producer retries/storage replication depend on selected service; replay/query restoration has manual restore work | Cheaper long-term storage can trade for longer recovery, query/reingestion costs, and more operations |


### Native LAW replication Considerations

Refer to the [workspace replication article][S1], however here are some important notes:

- The secondary LAW is a hidden **shadow**, not a normal workspace with another independently managed retention policy. Workspace configuration is replicated.
- Existing data is not backfilled. Initialization and schema changes can take up to an hour to start replicating.
- **It is highly opinionated on the Azure region and pair**. Select a currently supported primary/secondary pair within the same documented region group.
- For supported DCR-based inputs, attach the rule to the workspace's system DCE; a rule using that DCE must not target another workspace or Storage.
- During switchover, management changes (including retention, networking and new table/provider onboarding) are blocked. DNS changes can take longer for clients with persistent connections.
- **It does not support LAW-backed Application Insights**, VM Insights, and Container Insights. Custom DCR are not replicated in some scenarios.
- Log-search alert resources are not replicated automatically. Their regional execution/management dependencies (e.g. custom alert rules) can still fail.
- Replication is asynchronous. After switchover, reverse replication can lag, with the documented secondary buffer is bounded (up to 11 days when primary processing is unavailable).

See the [LAW recovery runbook](../runbooks/monitoring/log-analytics-recovery.md).

### Dual Ingestion (across 2 LAWs/AIs/AWS) Considerations

**App Insights:**

Note that Application Insights is used by the following with different methods of configurations
- The applications via connection string
- API Management (API Logger resource)
- CCaaS integration


**Azure Diagnostics:**

* Two settings on one resource protect against some **destination** failureswhile both still depend on that resource's telemetry generation/export pipeline. A regional service might keep serving or fail over its data plane without delivering every diagnostic record. For example, if a Redis region fails, the original region might not be logging telemetry to either of the configured LAW instances.

* Azure Diagnostic settings select categories, not actual log row filters. If only a subset of logs/metrics need to be replicated A secondary that needs only errors from a high-volume category needs a supported workspace transformation, or a separately engineered collector. For example the secondary LAW only needs specific error data, additional collection rules would need to configured.  [S3]


## Source and target considerations

### Azure Resource diagnostic settings and platform metrics

| Source and scope | Configure and target | Automatic behavior | Manual or custom-automated recovery; cost caution |
| --- | --- | --- | --- |
| **Front Door Standard/Premium**, global profile | Profile diagnostic settings for access, WAF and health-probe logs; critical categories to LAW-A and LAW-B, verbose access logs according to volume policy [S7] | Existing settings continue to their configured destinations; edge/origin traffic failover does not change them | Switch queries/rules to a healthy copy or retarget the profile setting. No per-origin-region logging target switch. Preserve `X-Azure-Ref` |
| **Cosmos DB**, account covering its configured regions | Account-level settings; resource-specific tables where supported; choose API-appropriate request, control-plane and partition/query categories [S8] | Database region failover does not select a new LAW | One account's logs can include multiple serving regions: preserve that dimension and use dual critical categories if required. Avoid full query text. Current Cosmos guide says metric-to-logs export is unsupported; validate rather than copying the template's `AllMetrics` |
| **Azure Managed Redis**, each regional resource/database | Database child scope (`redisEnterprise/.../databases/default`) for `ConnectionEvents` to LAW-region and optional remote LAW; verify metric scope separately [S9] | Geo-replication of cache data does not replicate the diagnostic-setting policy across separate resources | Configure each regional member. `REDConnectionEvents` captures connection/auth events, **not Redis commands or call-state history**. Changing a cache endpoint does not change its log destination |
| **AKS control plane**, each cluster | Cluster diagnostic settings for selected API/audit/control-plane categories to LAW-region; duplicate approved audit/critical categories only [S3] | Existing settings continue independently of application pod scheduling | Retarget per cluster or query dual copy. These settings do not route container stdout, OTel spans, or Prometheus. Full audit logs can dominate volume |
| **APIM platform logs**, each APIM service | Resource diagnostic settings to LAW-region, optional critical remote copy; validate gateway log category/table support for actual SKU [S3] | Workload gateway failover does not change diagnostic targets | Update the APIM resource setting, not the Application Insights logger. |
| **ACS**, each logical global resource with a data geography | ACS diagnostic settings to selected LAWs; Call Automation operational/incoming/outgoing/media-related categories required for correlation [S10] | ACS service placement is not a customer LAW failover policy | Dual critical categories or supported LAW strategy; preserve call/server/operation correlation. A global ARM location does not mean all data geographies are permitted. Do not confuse resource logs with raw audio |
| **Event Grid**, actual system topic/topic/domain/namespace | ACS **system topic** settings for `DeliveryFailures`; select other categories only if exposed by that resource type [S11] | Event delivery retries and dead-lettering operate on events, not LAW failover | Configure each topic scope; system topics do not inherit custom-topic `PublishFailures` support. Dead-lettered call events are not a backup of diagnostic logs |
| **App Configuration**, each store and any deployed replicas | Store diagnostic settings for audit and HTTP request logs to approved LAWs; validate replica attribution and actual scope [S12] | Geo-replication/provider endpoint failover concerns configuration values, not choosing a new telemetry destination | Separate regional stores need separate settings/config updates. `AACAudit` is distinct from aggregated `AACHttpRequest` (`HitCount` matters). Check network-security-perimeter destination constraints |
| **Key Vault**, each regional vault | Audit settings to LAW-region, second approved audit copy if required; subscription Activity Log for control changes [S3] | Vault recovery does not repoint log settings | Preserve security retention and independent access. Never export secret values; vault key/secret availability is a separate recovery dependency |
| **Storage**, account and blob/file/queue/table service scopes | Select diagnostic-enabled child services, not just the account; LAW-region for operational logs; approved archive separately [S3] | Storage data redundancy is not LAW redundancy | ZRS protects zones, not another region; geo-redundant archive recovery has its own RPO and access steps. The existing event dead-letter container is not a telemetry archive |
| **ACR**, shared/geo-replicated registry | Registry diagnostic categories supported by the deployed tier (login/repository events); selected logs to operational LAWs [S3] | Image replication does not create another LAW or duplicate alert rules | Query shared resource logs with location/operation context; do not assume one setting per image replica |
| **Other regional AI/network/compute resources**: Foundry/AI Services, Speech, Search, ACA, ingress/load balancer/NAT, private connectivity | Inventory the **actual resource type/SKU** and supported metrics/log categories; apply regional routing to diagnostic-enabled resources [S3], [S13] | Native metrics may exist without any diagnostic setting; SDK spans are a separate producer | Distinguish model/service resource logs, hosted-agent traces, ACA console/system logs, application OTel, and network flow data. No blanket `allLogs` policy or claim that all these surfaces are deployed |
| **Subscription Activity Log / Service Health** | Export approved subscription Activity Log categories and configure independent health notifications [S3] | Collection of Activity Log/platform metrics is automatic; export/alerts must be configured | Include subscription-level scope in inventory. Resource settings alone miss control-plane events at that scope |
| **Native Azure platform metrics**, all supported services | Query/alert in Azure Monitor Metrics by default; export only where a log join or retention requirement justifies it [S3] | Platform collection does not depend on a LAW diagnostic setting | `AllMetrics` is not a Prometheus backup; only exportable metrics are supported and diagnostic export flattens dimensions. Preserve dimensioned Metrics API/query paths for incident diagnosis |

NOTE: For regional sources, diagnostic export to **Storage/Event Hubs must be in the source region**; LAW does not have that same restriction. Global resources have different locality constraints: validate the actual provider and residency policy, not an application origin's region. Storage/Event Hubs firewall/trusted-service requirements also apply.[S3], [S9]

### AKS non-diagnostic-setting logs and metrics

| Source | Configuration and target | Automatic behavior | Customer recovery and limits |
| --- | --- | --- | --- |
| **Container Insights default streams** | Logs add-on, ConfigMap and default `ContainerInsights` DCR to one LAW; verify `ContainerLogV2`, inventory/events and other enabled streams [S14] | Healthy agents continue collecting; DCR changes do not inherently require agent restart | Repair/retarget through the supported onboarding/configuration path. A Kubernetes DCR's single-LAW limit is not the generic AMA ten-destination limit |
| **Container stdout/stderr multihoming** | High-scale mode plus multitenancy ConfigMap and separate namespace-specific `ContainerLogV2Extension` DCRs; repeat namespaces for each LAW destination [S15] | The agent discovers DCR associations every five minutes after startup; configured copies flow concurrently | This is a documented exception, not blanket Container Insights dual-homing. Maximum 30 extension-DCR associations; other streams remain on the default route. Validate the intended regional pairing before calling it cross-region DR |
| **Managed Prometheus metrics** | Metrics add-on, scrape config, per-destination DCR/DCE to AMW; documented multi-workspace pattern uses scrape labels and DCR filters [S16] | Collection follows configured filters/destinations; managed service operation is not proof of regional endpoint failover | The example partitions series between workspaces, not a complete failover/duplication design. Validate every critical default/custom target at each alternate |
| **Self-managed Prometheus remote write** | Native `remote_write` to an AMW ingestion endpoint, with supported authentication/version and Monitoring Metrics Publisher on the destination DCR [S17] | Configured sender retries apply within its queue/WAL and source-lifetime constraints | Multiple independent destinations are a candidate customer-managed design, not a verified Azure automatic-DR mechanism. Test fan-out, persistent buffering, prolonged outage and replay; include extra collector operations/cost |
| **ACNS network metrics** | Managed Prometheus default network targets and enabled ACNS Hubble/Cilium targets; check data-plane/version and scrape configuration [S25] | Metrics follow the selected Prometheus path, not a log diagnostic setting | Restore the actual target/scrape/DCR/AMW path. A healthy node metric does not prove pod-level or DNS visibility |
| **ACNS stored flow logs** | ACNS + `ContainerNetworkLog` custom resource -> bounded node files -> optional Azure Monitor forwarding -> `ContainerNetworkLogs` in LAW [S20] | Generation/aggregation and configured forwarding run automatically; no custom resource means no generated flow logs | Workspace switching can require **manual DCR update** or logs stop. Console-log multihoming does not establish flow-log multihoming. A separately managed collector is possible but needs its own delivery/DR design |
| **Application OTel, Istio/access logs and custom exporters** | Identify each emitter: stdout follows the container-log path; scraped metrics follow Prometheus; SDK/collector spans follow the configured exporter | Only the configured pipeline's retry/routing behavior applies | Recover each independently. Enabling Container Insights or ACNS does not automatically instrument/export every application or mesh signal |

#### Container Insights: supported console duplication, not universal duplication

[Multitenant logging docs][S15] explicitly supports sending the same namespaces' stdout/stderr to multiple LAWs. Configure **high-scale onboarding**, not just a ConfigMap toggle, then add a separate `ContainerLogV2Extension` DCR/DCE and cluster association for each destination. Ensure the extra ingestion endpoints are reachable and verify a marker from each selected namespace in both workspaces. Preserve and test fallback behavior for unmatched namespaces.

The default DCR still handles the other enabled streams; console duplication does not automatically copy `KubeEvents`, inventory, `Perf`, syslog, or `ContainerNetworkLogs`. The multitenancy article's prose and sample comment disagree about the fallback boolean's meaning, so test matched/unmatched namespaces rather than copying that sentence into a fleet rollout.

High-scale mode introduces its own agent, DCE, Private Link, tooling and collection restrictions [S21]. There is also a documentation ambiguity: [Container Insights region mapping][S22] says the LAW must be in the same region with listed exceptions, while multitenant onboarding accepts separate cluster and workspace regions. **The multihoming capability is documented; arbitrary cross-region multihoming for the selected regions is a support-validation gate.** Do not conflate that gate with native LAW replication's explicit Insights exclusions.

#### Managed Prometheus: configure both data and query paths

An AMW is regional and separate from LAW. Microsoft documents 18-month Prometheus retention, rather than a LAW-style configurable short-retention standby. AMW deletion has no soft-delete recovery. Cost control therefore emphasizes sample volume, scrape frequency, label cardinality, query cost and selected duplication, not reducing a secondary AMW's retention. [S18]

The documented multiple-workspace example partitions custom metrics using `microsoft_metrics_account` scrape labels and `prometheusForwarder.labelIncludeFilter`. It is **not evidence that associating another generic DCR duplicates every managed scrape**. For customer-managed direct remote write, use the supported sender version/authentication, the actual destination ingestion endpoint, and pregranted DCR permissions; allow role-propagation time before an incident. [S16], [S17]

**A single PromQL query cannot span AMWs.** Use separate data sources/queries in Grafana, or a separately engineered aggregation layer. Prometheus rule groups target a particular AMW; the documented existing group's workspace selection cannot be changed. Precreate corresponding secondary groups instead of assuming that changing ingestion moves rules. Recording rules generate additional series in their selected workspace. [S18], [S19]

#### Advanced Container Networking: logs are not metrics

ACNS enablement alone does not establish stored network-log coverage. The current stored-log procedure requires Cilium, Kubernetes 1.33+, and at least one `ContainerNetworkLog` resource; check current preview/tooling/region requirements before adopting it. Azure Monitor forwarding is separate from generation. 

Files rotate at **50 MB per node**. Enabling forwarding later is not retrospective recovery; the setup guide says logs older than two minutes are not ingested. Changing the selected LAW does not automatically repair the flow-log DCR. Aggregation and throttling also reduce fidelity, so these logs must not be described as a lossless network audit trail. [S20], [S24]

For cost, narrow captured traffic and unnecessary network series first. Basic node-network metrics, paid ACNS pod-level features, and self-managed Retina OSS have different support/operational boundaries; inspect the installed feature set. Treat new source-side metric-filtering capabilities as version/region-gated rather than generally deployed behavior.

Use the [AKS telemetry recovery runbook](../runbooks/monitoring/aks-telemetry-recovery.md) to verify logs, metrics, and flow data separately.

### Application Insights, APIM, and SaaS export

| Producer/path | Configure and target | Automatic behavior | Manual or customer-automated recovery |
| --- | --- | --- | --- |
| **Application in AKS**, direct SDK | Complete connection string for regional AI -> same-region LAW; configure supported ingestion authentication independently [S26], [S27] | Supported exporters retry/buffer transient failures; no verified automatic choice of another AI component | Update the actual configuration source and reload/reinitialize/roll out the exporter safely. App Configuration refresh alone is not proof of cutover |
| **Application via collector** | Explicit supported exporter/backend, routing map, queue/WAL and identity; regional AI/LAW pair | Configured queue/retry behavior only | Change the collector route through its supported config mechanism. Fan-out, source survival and delivery guarantees require tests; a collector does not intercept APIM or SaaS exporters automatically |
| **APIM Application Insights telemetry** | `applicationInsights` logger plus service/API diagnostic resource referencing its `loggerId`; separate from Azure resource diagnostic settings [S28] | More-specific API diagnostics normally override All APIs; gateway traffic recovery does not choose another logger | Automate supported logger/diagnostic ARM operations or use approved admin steps. Inspect every override. Multiplexing to two distinct loggers requires Microsoft Support enablement; it is not the default |
| **Dynamics Customer Service/Contact Center conversation diagnostics** | Power Platform admin center export type **Dynamics Customer Service**, one export configuration per managed environment -> dedicated environment AI and its LAW [S29], [S30] | The managed export sends lifecycle events to its configured component, not the application's App Configuration target | Manual administration/replacement is the documented recovery baseline. No documented dual-export or automatic regional target selection was established |
| **Dataverse/model-driven app diagnostics** | Separate **Dataverse diagnostics and performance** export package -> environment-specific AI; `CDS Data Export` role-instance identifies this exported telemetry [S31], [S32] | Platform exports selected telemetry asynchronously in AI schema | Restore or replace the export package via supported administration; verify source type and filters. Do not infer it has the same event schema as conversation diagnostics |
| **Power Automate/Copilot Studio exports, if adopted** | Select the actual product export type and supported event/filter choices in the admin center [S32] | Feature-specific managed export | Separate validation of eligibility, release status, destinations and latency; not automatically covered by a Customer Service export |
| **Browser SDK, custom tools, biometrics and Foundry tracing, if configured** | Inventory direct SDK/exporter and project-connection settings; route with the owning regional application where appropriate | Only that producer's implemented refresh/retry behavior | Update each real producer. |
| **Teams Phone/CQD/Graph call records** | Existing design defers the collector/ETL; these are not native AI distributed spans | No repository-implemented telemetry failover | A future collector needs durable checkpoints, supported polling/subscription renewal, destination switching and deduplication. Do not claim LAW replication restores records that were never collected |

#### Application Insights component and workspace are separate boundaries

Microsoft's Well-Architected guide recommends placing Application Insights and its backing LAW in the **same region**: splitting one pair across regions adds two regional dependencies rather than creating redundancy. This proposal's per-region pairs extend the normal per-workload/per-environment baseline for regional isolation. [S26]

Compare three distinct actions:

1. **New component plus new LAW:** provides an independent ingestion/storage target, once networking, authentication, dashboards and rules are prepared. Change producer routing to send future telemetry there.
2. **Change the existing component's associated LAW:** documented under **Properties > Change workspace** and management APIs. This changes the backing-workspace association, not the component's location or the producer's component identity. It is not full component/region failover. Preserve all required properties when using create-or-update APIs. Historical records are not copied into the new workspace. [S34]
3. **Diagnostic export from AI to another LAW/Storage:** provides a downstream copy of supported telemetry, but depends on the original AI ingestion/export path. It is not a second live AI ingestion endpoint. The destination LAW must differ from the backing LAW; follow Microsoft's workspace-access guidance to avoid duplicated application maps/transactions when both copies are visible. Legacy continuous export is not the workspace-based mechanism. [S3], [S34]

The [FAQ][S27] explicitly says an existing Application Insights resource cannot be moved between regions and historical data cannot be migrated that way. Create the new component, recreate customizations, update connection strings, and test. Preserve the original component/workspace for investigations instead of deleting it during an incident.

Supported OpenTelemetry exporters can use offline storage and retries [S35]. This is a bounded delivery mechanism, not a remote backup: node-local storage, process termination, disk permissions/capacity and queue expiration matter. Also distinguish Live Metrics, preaggregated metrics, retained logs and availability tests; one working surface does not establish that every surface recovered.

#### APIM logger configuration and automation

**Recommended baseline:** each independently deployed regional APIM service logs to its regional Application Insights component, backed by an independent LAW in that same region. Separate components sharing one LAW do not provide independent storage/query recovery.

| Deployment or incident | Recommended logger behavior |
| --- | --- |
| Separate APIM services in A and B | APIM-A -> AI-A -> LAW-A; APIM-B -> AI-B -> LAW-B |
| APIM-A fails and traffic moves to healthy APIM-B | Leave APIM-B's healthy logger unchanged |
| APIM-A remains healthy but AI-A/LAW-A is unavailable | Switch APIM-A's effective diagnostics to a precreated alternate logger targeting AI-B/LAW-B or another approved independent pair |
| One APIM service with multiple regional gateways | Logger and service/API diagnostic scopes are not independent per-region routing policies; a shared diagnostic change can affect all gateways using it |

**Prepare before an incident**

- Create primary and alternate `applicationInsights` logger resources in each   APIM service that requires destination switching. Creating two loggers does   not enable dual logging or automatic failover.
- Use the documented connection-string-plus-managed-identity configuration.   Grant the selected identity Monitoring Metrics Publisher on each target   Application Insights component. A connection string does not grant access.   The portal's logger-creation flow currently uses an instrumentation key;   use the documented REST/Bicep/ARM configuration for managed identity. [S28]
- Inventory the All APIs diagnostic and every API/version-specific override.   Record their current `loggerId` references and writable settings.
- Validate alternate capacity, residency, query access and ingestion connectivity.   For private-only AMPLS ingestion, verify both the alternate component and LAW.   Prepare secondary queries, alerts and notification ownership.

**Switch during a telemetry-destination incident**

1. Confirm the failing dependency. Do not change healthy loggers for an APIM    workload outage or a dashboard-only problem. Test the alternate destination.
2. Obtain approval and save the affected diagnostic configurations, resource
   versions and UTC change boundary. Ensure the recovery operator can manage APIM from outside the failed region.
3. Through the supported service/API diagnostic management API, update the effective `loggerId` to the alternate logger's full ARM resource ID. Change All APIs and each overriding API diagnostic that needs recovery. Use conditional updates where supported; preserve sampling, correlation, verbosity, error logging and header/body capture settings.
4. Read back every changed scope and allow gateway configuration propagation. Generate a harmless request through each affected API/gateway and confirm fresh telemetry at the alternate. API success alone does not prove recovery.
5. Verify correlation, direct queries and actual alert delivery. Select one authoritative paging path and retain access to both historical destinations.

Prefer switching diagnostic references over changing a shared logger's
credentials: changing that logger affects every diagnostic referencing it.
Neither action moves historical records or guarantees replay of buffered data.
Configuration propagation and recovery time must be measured in a rehearsal.

**Failback:** after sustained primary health and approval, restore the saved
logger references in controlled cohorts and repeat the same verification.
Reconcile the final state with IaC so deployment automation does not undo it.

APIM resource diagnostic settings are a separate LAW pipeline and are not changed
by switching an Application Insights logger. All APIs and per-API loggers do not
provide dual logging by default; documented multiplexing requires Microsoft
Support enablement. APIM workspace loggers also have a separate configuration/
authentication support boundary. [S28]

**APIM as an ingestion proxy is a different proposal.** Do not introduce it as
the default failover mechanism for application SDK or Power Platform telemetry.
Protocol compatibility, authentication, retries, throughput, regional resilience,
supportability and cost remain unvalidated. Prefer supported producer destination
changes; APIM's own logger configuration does not retarget those other producers.

Use the [Application Insights/APIM recovery runbook](../runbooks/monitoring/application-insights-retargeting.md).

#### Dynamics and Power Platform: supported administration, not assumed hot failover

The supplied Dynamics and Power Platform conversation guides describe the same
Customer Service export: **Manage > Data export > App Insights > New data export >
Dynamics Customer Service**, select environment, then target subscription,
resource group and Application Insights. Only one Customer Service export is
allowed per environment; the two articles are not two independent export channels.
[S29], [S30]

The broader export setup guide adds requirements and constraints [S32]:

- Managed Environment, relevant licensing, Azure destination permissions, and the documented tenant-level **and** environment-level administrative roles.
- **Local authentication is required** for this integration. An Entra-only application AI component may therefore be an unsuitable shared destination. Prefer a separately governed SaaS component rather than weakening all regional application sinks. Confirm private-ingestion reachability for the SaaS exporter; do not assume it uses the AKS private network or managed identity.
- Use an environment-specific component. Out-of-the-box reports do not work correctly when telemetry from multiple environments is mixed in one component.
- The guide states a **24-hour delivery SLA**, and initial export can take up to 24 hours. This is not an end-to-end failover RTO guarantee. The Dynamics dashboard separately documents approximately 24-hour initial synchronization and up to 15-minute dashboard delay; neither proves all events arrive in real time. [S36]
- Cloud/preview restrictions differ in wording across the guides. Confirm actual commercial/sovereign-cloud and product availability; do not extrapolate commercial support to GCC, GCC High, DoD or China.

The reviewed administration guide documents **create** and **delete** export
packages and creating a new connection to resume exporting. It does not document
an in-place retarget API or automatic regional switchover for these exports.
Therefore use **approved manual replacement** as the baseline, verifying the
tenant's supported workflow before deleting anything. This is not a claim that
automation can never become available: production automation is gated on a
documented supported API, authentication, concurrency/rollback behavior and a
tenant rehearsal. Do not substitute App Center export APIs, classic AI continuous
export, or private admin-center endpoints.

Do not promise backlog replay, exported completeness, or transfer-context fields
that have not been tested. The general integration overview describes Dataverse
performance/failure telemetry; it is not a complete call ledger. The Dynamics
Diagnose dashboard excludes transfer/consult diagnostics, so validate raw events
and each correlation field independently instead of claiming custom variables
necessarily appear in every exported event. [S31], [S36]

Operational procedures: [Application Insights/APIM retargeting](../runbooks/monitoring/application-insights-retargeting.md)
and [Power Platform export recovery](../runbooks/monitoring/power-platform-telemetry-recovery.md).

### Global versus regional routing rule

**Regional resource:** bind to its region's operational sink to isolate failures;
add a second critical stream only when preserving that region's evidence is worth
the extra cost. A surviving Redis/AKS resource in B keeps using its own settings
even when A fails.

**Global or multiregion resource:** choose destinations based on telemetry
resilience and residency, not current traffic location. One Front Door profile
or Cosmos account is not recreated for each serving region. Dual-write selected
categories ahead of time so querying B does not require management changes to a
potentially impaired source during the outage.


## Query, alert, and dashboard resilience

- Keep a regional data-source selector for LAW/AMW and an explicit list of reachable sources. LAW queries can span independent workspaces; a single PromQL query cannot span AMWs. A failed source must not silently disappear from availability/completeness reports.
- Design recovery queries before an incident. Cross-workspace querying is a federation mechanism, not data replication. Some single-workspace queries in [queries.md](queries.md) need adaptation for this proposal.
- Native Application Insights queries use names such as `traces` and `customDimensions`; workspace queries use `AppTraces` and `Properties`. Verify schema at the query surface rather than copying field names across both.
- Prefer one authoritative copy of duplicated data for aggregate counts. Reconciliation across copies needs an actual source event/span ID and source identity; `e2e_call_id` alone identifies a **call**, not a unique event. Avoid summing identical Prometheus series from two workspaces.
- Preserve region, cluster, service/role, operation/trace ID, call correlation and routing version. A failover can split one call across old/new destinations.
- Provision secondary Grafana configuration, dashboards/data sources, identity grants, and network access independently. Zone redundancy is not a substitute for a regional dashboard recovery path. Managed Grafana's supported zone redundancy is a billable Standard-tier, **creation-time** option, not an in-place toggle on the repository's existing non-zone-redundant declaration. Microsoft handles its in-region recovery; customers manage separate regional Grafana workspaces, configuration synchronization and user routing. [S23]
- Deploy/test log-search and Prometheus alert rules, action groups and downstream incident integrations for the secondary path. Define primary/secondary paging ownership and test notification delivery, not just rule existence.
- Keep direct query and independent synthetic/Service Health access when Grafana or the primary query service is unavailable. Silence is not success.

### Notes on Archive and replay options 

Direct diagnostic export to an approved Storage/Event Hubs destination can
provide another copy of eligible source logs; it still shares source generation
and destination-region constraints. An Event Hubs **metadata** DR configuration
must not be mistaken for message-data replication. Choose and validate its
actual data-replication mode if used.

[LAW data export][S5] exports selected supported tables as new data arrives; it is
not historical backup or ingestion failover. The destination is in the workspace
region, export is chargeable, and retries/buffering are bounded. Geo-redundant
Storage may protect exported objects, not records that never reached export.
Recovery needs a documented query/replay path, schema mapping, identity and
timestamp preservation, deduplication, and reingestion cost. Do not assume
replayed custom records recreate the original Application Insights experience.

# Appendix: Documentation References

Sources were last reviewed on 2026-09-14 unless otherwise specified. Verify live availability, SKU/region support,
permissions, pricing and export behavior.

| ID | Source and use |
| --- | --- |
| S1 | [LAW workspace replication][S1]: switchover, exclusions, DCE requirements, permissions, pricing warning, failback |
| S2 | [Reliability in Azure Monitor Logs][S2]: zones versus regional recovery, asynchronous replication, independent alert resources |
| S3 | [Diagnostic settings][S3]: destinations, five-setting limit, category/metric restrictions, initial flow latency |
| S4 | [Azure Monitor Logs cost calculations][S4]: ingestion versus retention, plans, commitment and export charges |
| S5 | [LAW data export][S5]: export scope, limitations and downstream archive boundaries |
| S6 | [LAW retention][S6]: table overrides, included retention and analytics versus total retention |
| S7 | [Front Door monitoring][S7]: global access/WAF/probe logs and tracking reference |
| S8 | [Cosmos DB resource logs][S8]: account scope, resource-specific mode and metric-export caveat |
| S9 | [Managed Redis diagnostics][S9]: database-scoped connection events and destination locality |
| S10 | [ACS Call Automation logs][S10]: operational/media/correlation records |
| S11 | [Event Grid system-topic logs][S11]: actual supported category |
| S12 | [App Configuration monitoring][S12]: audit versus HTTP requests and perimeter constraints |
| S13 | [Supported resource logs][S13]: category, table-plan and transformation support per resource type |
| S14 | [Kubernetes collection configuration][S14]: distinct DCR streams and destination limits |
| S15 | [Container Insights multitenancy/multihoming][S15]: supported namespace-based console duplication |
| S16 | [Prometheus multiple-workspace routing][S16]: scrape labels and per-destination filters; not turnkey DR |
| S17 | [Prometheus remote write][S17]: endpoint, sender-version and identity prerequisites |
| S18 | [AMW overview][S18]: regional storage, retention, query boundaries and management |
| S19 | [Prometheus rule groups][S19]: workspace-scoped rule configuration |
| S20 | [ACNS stored-log setup][S20]: generation, forwarding, limits and workspace-change warning |
| S21 | [Container Insights high-scale collection][S21]: onboarding and network prerequisites |
| S22 | [Container Insights region mapping][S22]: region constraint requiring topology validation |
| S23 | [Managed Grafana reliability][S23]: zone and customer-managed regional recovery |
| S24 | [ACNS flow-log behavior][S24]: aggregation and fidelity limitations |
| S25 | [Prometheus default scrape targets][S25]: actual network/cluster scrape coverage |
| S26 | [Application Insights Well-Architected guidance][S26]: paired component/LAW regions, failure analysis, cost and recovery planning |
| S27 | [Application Insights FAQ][S27]: new-region resource recreation, historical-data boundary, query schema and buffering |
| S28 | [APIM Application Insights integration][S28]: logger/diagnostic scopes, managed identity, multiplexing and sampling |
| S29 | [Dynamics conversation diagnostics][S29]: eligibility, one export/environment and lifecycle schema |
| S30 | [Power Platform conversation diagnostics][S30]: Customer Service export setup and licensing |
| S31 | [Power Platform integration overview][S31]: Dataverse export semantics and common fields |
| S32 | [Power Platform export setup][S32]: local auth, admin roles, 24-hour delivery and create/delete workflow |
| S33 | [App Configuration geo-replication][S33]: eventual configuration synchronization versus application destination choice |
| S34 | [Create/configure Application Insights][S34]: workspace association and workspace-based export |
| S35 | [Azure Monitor OpenTelemetry configuration][S35]: exporter configuration, offline storage and retries |
| S36 | [Dynamics Diagnose dashboard][S36]: synchronization delay and transfer/consult limitations |

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