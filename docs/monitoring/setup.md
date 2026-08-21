# Setup Runbook

Light up each telemetry source and the Azure Managed Grafana single pane. Do these once per environment. Order doesn't matter except that Grafana needs the sinks to exist first.

Prerequisites: the shared **Log Analytics workspace** and **Application Insights** already exist (declared in the AppHost). Recommendation: point the IVR's Application Insights and the D365 export at the **same** Log Analytics workspace so the single-call-trace query is a single-workspace union.

---

## 1. IVR app — already wired

Nothing to configure. `ServiceDefaults.ConfigureOpenTelemetry()` calls `AddCallCorrelation()`, which:

- registers the ambient correlation accessor + store, and
- adds the OpenTelemetry processor that stamps the canonical ids onto every span/log.

The ingress mint and per-callback re-hydration live in `CallingApi`. For **multi-pod** deployments, switch the store to Redis so ingress and later webhooks resolve the same context regardless of which pod they land on:

```csharp
// in the IVR host, after AddServiceDefaults()
builder.Services.AddRedisCallCorrelationStore(); // requires a registered IConnectionMultiplexer
```

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

ACS is bound to Teams Phone via Teams Phone Extensibility and lives **outside** the Aspire graph, so enable its diagnostic settings directly on the ACS resource. The log categories and resulting table names are captured in `Agents.AI.Monitoring/Azure/AcsMonitoring.cs`.

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
2. Create (or reuse) an Application Insights instance — ideally the same one the IVR uses.
3. In the **Power Platform admin center**, connect Customer Service to that Application Insights instance. There is **one** conversation-diagnostics export per environment.

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

Names are **case-sensitive** and must match exactly. These values then appear in the conversation-diagnostics `customDimensions`, giving you `cc_contextId == context_id == e2e_call_id`.

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

For automated/repeatable provisioning of the Grafana instance itself — identity, Managed Prometheus integration, and the Monitoring Reader grant — use the Bicep module `Agents.AI.Monitoring/Azure/Infra/grafana.bicep` instead of the CLI steps above.

See [dashboards.md](dashboards.md) for the panels and the composed dashboards.

---

## 5. Local development

`AppHost.AddMonitoring()` runs an OSS OpenTelemetry stack (Prometheus + OpenTelemetry Collector + Grafana) as containers **in run mode only**. In publish mode it is a no-op — the Azure Monitor sinks and Managed Grafana above take over. No action needed for local dev beyond `azd`/Aspire run.
