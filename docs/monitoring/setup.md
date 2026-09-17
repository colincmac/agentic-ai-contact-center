# Setup Runbook

Light up each telemetry source and the Azure Managed Grafana single pane. Do these once per environment. Order doesn't matter except that Grafana needs the sinks to exist first.

Prerequisites: verify the **Log Analytics workspace** and **Application Insights**
destinations exist in the intended environment. The
[platform template](../../infra/modules/platform.bicep) declares shared sinks;
the current [AppHost](../../code/ContactCenter.AppHost/AppHost.cs) does not provision
them. Sharing the IVR and D365 workspace simplifies queries, but is not a regional
HA/DR design. Select targets using the
[HA/DR research](high-availability.md) and
[proposed ADR-0017](../adr/0017-telemetry-high-availability-and-disaster-recovery.md).
Neither these setup steps nor the infrastructure source establish deployed
telemetry or successful failover.

---

## 1. IVR app — verify instrumentation and export

Review [service defaults](../../code/ContactCenter.ServiceDefaults/Extensions.cs)
and the [banking demo coordinator](../../code/ContactCenter.AIAgent/Services/CallCoordinator.cs)
for the actual host integration. Do not assume service defaults alone register
the contact-center correlation pipeline, select an Azure exporter, or dynamically
reload an Application Insights destination.

Confirm the runtime exporter and destination, generate a synthetic call, and
verify both correlation and ingestion. For multiregion operation, validate each
host/collector separately and retain cluster/region identity. Use the
[Application Insights retargeting runbook](../runbooks/monitoring/application-insights-retargeting.md)
for recovery rather than assuming an App Configuration change immediately
reconfigures the exporter.

Confirm the ids are flowing (App Insights):

```kusto
AppTraces
| where TimeGenerated > ago(15m)
| where isnotempty(tostring(Properties["e2e.call_id"]))
| project TimeGenerated, e2e = tostring(Properties["e2e.call_id"]), Message
| take 20
```

---

## 2. ACS — diagnostic settings → Log Analytics

Enable diagnostic settings directly on the ACS resource bound to Teams Phone.
The [platform template](../../infra/modules/platform.bicep) declares ACS, but
source presence does not establish that its diagnostic settings are deployed.
Verify the current categories against the
[ACS Call Automation log reference](https://learn.microsoft.com/en-us/azure/communication-services/concepts/analytics/logs/call-automation-logs).
The examples below demonstrate one destination; select only supported categories
and add an independently configured second setting if the HA/DR policy requires it.
Do not export `AllMetrics` unless the metric-to-log copy is needed.

### Option A — Azure CLI

```bash
az monitor diagnostic-settings create \
  --name acs-to-law \
  --resource "<ACS_RESOURCE_ID>" \
  --workspace "<LOG_ANALYTICS_WORKSPACE_RESOURCE_ID>" \
  --logs '[{"categoryGroup":"allLogs","enabled":true}]' \
  --metrics '[{"category":"AllMetrics","enabled":true}]'
```

### Option B — Bicep

```bicep
@description('Resource id of the existing ACS resource bound to Teams Phone via TPE.')
param acsResourceId string

@description('Resource id of the shared Log Analytics workspace.')
param logAnalyticsWorkspaceId string

resource acs 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: last(split(acsResourceId, '/'))
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'acs-to-law'
  scope: acs
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
```

The Call Automation logs you care about for correlation (Operational, Media Summary, Media Streaming usage) land in tables such as `ACSCallAutomationIncomingOperations`, which expose `CorrelationId`, `CallConnectionId`, `ServerCallId`, and `OperationId`.

Reference: [Azure Communication Services logs](https://learn.microsoft.com/azure/communication-services/concepts/analytics/logs/call-automation-logs).

---

## 3. Dynamics 365 Contact Center — conversation diagnostics

This exports **your** conversation lifecycle telemetry to Application Insights so you can correlate it with the rest of the call. It does not require any knowledge of Dynamics internals — you only configure the export and read the documented fields.

### Enable the export

1. Ensure the environment is a **Managed environment**.
2. Verify licensing, tenant/environment administrator roles, and Azure
   destination permissions. Prefer a dedicated environment-specific Application
   Insights component: the [export guide](https://learn.microsoft.com/en-us/power-platform/admin/set-up-export-application-insights)
   requires local authentication and warns against combining multiple environments
   in one component. Do not weaken an Entra-only IVR destination implicitly.
3. In **Power Platform admin center > Manage > Data export > App Insights**,
   create the **Dynamics Customer Service** export for that environment and
   destination. There is **one** conversation-diagnostics export per environment.
   Dataverse diagnostics/performance is a separate export type.

Allow up to 24 hours for initial export; the general export guide states a
24-hour telemetry-delivery SLA, not a rapid failover guarantee. For destination
replacement and verification, use the
[Power Platform recovery runbook](../runbooks/monitoring/power-platform-telemetry-recovery.md).

Steps: [Configure conversation diagnostics](https://learn.microsoft.com/dynamics365/customer-service/administer/configure-conversation-diagnostics).

Once enabled, conversation lifecycle events land in the App Insights **`Traces`** table, with identifiers in `customDimensions`:

| `customDimensions` key | Meaning (for correlation) |
|---|---|
| `powerplatform.analytics.resource.id` | The conversation / work-item id |
| `powerplatform.analytics.subscenario` | The routing stage (e.g. classification, route-to-queue) |
| `omnichannel.call.id` | The call id (secondary match key) |

### Declare context variables so the transfer joins back

To join a D365 conversation back to the IVR call, declare **context variables whose names exactly match** the transfer header keys the IVR sends (see [correlation-model.md](correlation-model.md#carrying-context_id-across-the-transfer)):

| Context variable | Carries |
|---|---|
| `cc_contextId` | the IVR `context_id` (primary join key) |
| `cc_e2eCallId` | the IVR `e2e_call_id` |
| `cc_intent`, `cc_lang`, `cc_workstream` | optional routing/context hints |

Names are **case-sensitive** and must match exactly. This is the proposed
correlation contract, not proof that every transfer context variable is exported
in every conversation-diagnostics event. Verify the fields against raw exported
events for the selected transfer/channel scenario. Treat absent context as an
integration gap rather than claiming `cc_contextId == context_id == e2e_call_id`
has been demonstrated in live telemetry.

### Note on the productized dashboard

The Dynamics **Diagnose dashboard** is convenient for routing/assignment analysis but **does not cover transfer/consult conversations** — and your flow is a transfer. Reconstruct the hand-off from the raw App Insights `Traces` via the queries in [queries.md](queries.md). Reference: [Diagnose contact center health with the Application Insights dashboard](https://learn.microsoft.com/dynamics365/contact-center/use/diagnose-dashboard).

---

## 4. Azure Managed Grafana — the single pane

Provision once per environment. Grafana queries every source through its built-in **Azure Monitor** data source using its managed identity, so the key step is RBAC.

### Provision + grant access

```bash
# Create the instance (its system-assigned identity is created with it).
az grafana create --name <grafana-name> --resource-group <rg> --location <loc>

# Let Grafana READ Azure Monitor (Log Analytics, App Insights, metrics) across the scope you query.
GRAFANA_MI=$(az grafana show --name <grafana-name> --resource-group <rg> --query identity.principalId -o tsv)
az role assignment create --assignee "$GRAFANA_MI" --role "Monitoring Reader" --scope "<subscription-or-rg-scope>"

# Give your operators access to Grafana itself.
az role assignment create --assignee "<user-or-group-object-id>" --role "Grafana Admin" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Dashboard/grafana/<grafana-name>"
```

For **Managed Prometheus** (IVR metrics), link the Azure Monitor workspace to Grafana:

```bash
az grafana update --name <grafana-name> --resource-group <rg> \
  --azure-monitor-workspace-integrations "<azure-monitor-workspace-resource-id>"
```

### Data sources

The built-in **Azure Monitor** data source exposes all four query types once Grafana has *Monitoring Reader*:

- **Logs** → Log Analytics (ACS logs) and App Insights (IVR + D365).
- **Traces** → Application Insights.
- **Metrics** and **Managed Prometheus** → IVR RED/USE.

### Import the dashboards

The reusable panel library, manifest, and generated dashboards described in
[dashboards.md](dashboards.md#the-panel-reuse-model) have not yet been migrated
into this repository. Do not run the assembler until those assets are present;
the current script under `scripts/monitoring/` is only the assembly entry point.

The original single pane (`contact-center-e2e.json`) can still be imported directly. After import, set the dashboard variables:

- `ds` → the **Azure Monitor** data source (logs/traces)
- `prom` → the **Managed Prometheus** data source (IVR metrics)
- `law` → the Log Analytics workspace resource id
- `e2e` → the `e2e.call_id` you want to trace (blank for fleet views)

The [platform template](../../infra/modules/platform.bicep) declares Grafana,
its managed Prometheus integration, and relevant assignments. Inspect the
[platform RBAC module](../../infra/modules/platform-rbac.bicep) and actual
deployment scopes before assuming it can read every log source. Secondary
Grafana, data-source access, and alert recovery require separate configuration;
see [HA/DR guidance](high-availability.md).

See [dashboards.md](dashboards.md) for the panels and the composed dashboards.

---

## 5. Local development

The current [AppHost](../../code/ContactCenter.AppHost/AppHost.cs) registers the
application project. Use the actual local Aspire telemetry configuration to
inspect instrumentation; do not assume an `AddMonitoring()` container stack or
automatic Azure sink handoff exists. Local dashboard output does not validate
Azure ingestion, independent regional sinks, or SaaS export.
