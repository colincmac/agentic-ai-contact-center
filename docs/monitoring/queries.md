# Queries

Copy-paste KQL for the single pane. The canonical assets live in `Agents.AI.Monitoring/Azure/Queries/*.kql` and `Agents.AI.Monitoring/Dynamics/Queries/*.kql` and are embedded in the library so they can ship with deployment tooling.

These assume the IVR's Application Insights and the D365 export share one workspace (see [setup.md](setup.md)). If they don't, run the D365 portion against the D365 workspace and correlate on `cc_contextId` at the reporting layer.

Table names use the workspace-based Application Insights schema (`AppTraces`, `AppRequests`, `AppDependencies`) and the ACS Log Analytics table `ACSCallAutomationIncomingOperations`.

---

## Trace one call end to end

The headline query: reconstruct a single call as one ordered timeline spanning ACS, the IVR, and Dynamics 365. Set `e2e` to the `e2e.call_id` you want.

```kusto
declare query_parameters(e2e:string = "");
let ivr =
    union AppDependencies, AppRequests, AppTraces
    | where tostring(Properties["e2e.call_id"]) == e2e
    | project TimestampUtc = TimeGenerated,
              Boundary = "IVR",
              Source = ItemType,
              Operation = coalesce(column_ifexists("Name", ""), column_ifexists("Message", "")),
              CallConnectionId = tostring(Properties["acs.call_connection_id"]),
              ContextId = tostring(Properties["call.context_id"]);
let connIds = toscalar(ivr | where isnotempty(CallConnectionId) | summarize make_set(CallConnectionId));
let ctxId = toscalar(ivr | where isnotempty(ContextId) | take 1 | project ContextId);
let acs =
    ACSCallAutomationIncomingOperations
    | where CallConnectionId in (connIds)
    | project TimestampUtc = TimeGenerated, Boundary = "ACS", Source = "CallAutomation",
              Operation = OperationName, CallConnectionId, ContextId = "";
let d365 =
    AppTraces
    | extend cd = parse_json(Properties)
    | where tostring(cd["cc_contextId"]) == ctxId and isnotempty(ctxId)
    | project TimestampUtc = TimeGenerated, Boundary = "D365", Source = "ConversationDiagnostics",
              Operation = tostring(cd["powerplatform.analytics.subscenario"]),
              CallConnectionId = "", ContextId = tostring(cd["cc_contextId"]);
union ivr, acs, d365
| order by TimestampUtc asc
```

**How the join works:** start from IVR spans tagged with the `e2e.call_id`; pull the ACS `CallConnectionId`(s) and the `context_id` off those spans; join ACS logs on `CallConnectionId` (stable) and D365 `Traces` on `cc_contextId`. Order by time for a single readable timeline.

**Don't know the `e2e.call_id`?** Look it up from any known identifier:

```kusto
// by hashed caller number over the last hour
AppTraces
| where TimeGenerated > ago(1h)
| where tostring(Properties["caller.ani_hash"]) == "<sha256-hex>"
| distinct e2e = tostring(Properties["e2e.call_id"]), did = tostring(Properties["call.called_did"])
```

---

## Trace a failed media-streaming upgrade (ingress 400)

The realtime media WebSocket is a **separate request** from the `IncomingCall` webhook, so its failures are traced by resolving the canonical `e2e.call_id` from an id you already have, then feeding that into the single-call-trace query.

**Failed inside the app** (accept / ownership / property-fetch): the span already carries `e2e.call_id` because the IVR hydrates correlation from the `x-ms-call-connection-id` header before accepting the socket. Find it directly:

```kusto
union AppRequests, AppDependencies, AppTraces
| where TimeGenerated > ago(24h)
| where Name has "media/wss" or tostring(Properties["error.type"]) == "ownership_refused"
| where tostring(Properties["acs.call_connection_id"]) == "<callConnectionId>"
| project TimeGenerated, e2e = tostring(Properties["e2e.call_id"]),
          error = tostring(Properties["error.type"]), Message, k8s_pod = tostring(Properties["k8s.pod.name"])
| order by TimeGenerated asc
```

**Failed at the ingress layer** (Front Door / APIM / Istio 4xx — the upgrade never reached the app): pull the `serverCallId` from the WSS URL query (the IVR appends `?serverCallId=…` to the media `TransportUri`) or the `x-ms-call-connection-id` header from the proxy access log, then resolve `e2e.call_id` from the IVR spans of the *same* call (the `IncomingCall` webhook did reach the app and carries `acs.server_call_id` + `e2e.call_id`):

```kusto
// key = serverCallId or callConnectionId taken from the ingress 4xx access log
let key = "<serverCallId-or-callConnectionId>";
union AppRequests, AppDependencies, AppTraces
| where TimeGenerated > ago(24h)
| where tostring(Properties["acs.server_call_id"]) == key
     or tostring(Properties["acs.call_connection_id"]) == key
| summarize e2e = take_any(tostring(Properties["e2e.call_id"])),
            ingressTrace = take_any(tostring(Properties["call.ingress_traceparent"]))
```

The `call.ingress_traceparent` also lets App Insights show the media-streaming trace linked to the ingress trace. (Routing ingress 4xx logs to the workspace and logging the `serverCallId`/`x-ms-call-connection-id` field is an infra-side prerequisite — Front Door/APIM diagnostics and the Envoy access-log format.)

---

## Transfer success — did the hand-off land?

Reconcile ACS transfer outcomes with Dynamics conversations created downstream, to catch dropped hand-offs (ACS accepted the transfer but no D365 conversation hydrated the context id).

```kusto
let acsTransfers =
    ACSCallAutomationIncomingOperations
    | where OperationName has_any ("Transfer", "CallTransferAccepted", "CallTransferFailed")
    | summarize
        TransfersAccepted = countif(OperationName has "Accepted" or ResultType == "Succeeded"),
        TransfersFailed   = countif(OperationName has "Failed" or ResultType == "Failed");
let d365 =
    AppTraces
    | extend cd = parse_json(Properties)
    | where tostring(cd["powerplatform.analytics.subscenario"]) has_any ("Classification", "RouteToQueue")
    | summarize D365ConversationsCreated = dcount(tostring(cd["cc_contextId"]));
acsTransfers
| extend d = 1
| join kind=fullouter (d365 | extend d = 1) on d
| project TransfersAccepted, TransfersFailed, D365ConversationsCreated,
          HandoffGap = TransfersAccepted - D365ConversationsCreated
```

A non-zero `HandoffGap` means transfers were accepted by ACS but the context id never showed up in D365 — the first thing to alert on.

---

## Live ops — IVR health (RED)

Rate / errors / latency for the IVR, binned for a time-series panel.

```kusto
AppRequests
| where TimeGenerated > ago(1h)
| summarize
    Requests = count(),
    Failures = countif(Success == false),
    P50Ms = percentile(DurationMs, 50),
    P95Ms = percentile(DurationMs, 95),
    P99Ms = percentile(DurationMs, 99)
    by bin(TimeGenerated, 1m)
| extend ErrorRate = iff(Requests == 0, 0.0, todouble(Failures) / Requests)
| order by TimeGenerated asc
```

---

## Metrics — Managed Prometheus (PromQL)

The KQL above reads **logs/traces** (App Insights + ACS). The RED/USE metric panels read **Managed Prometheus** instead — the IVR's OpenTelemetry meters (`CallingTelemetry`, the GenAI conversation session) exported through the Azure Monitor workspace. These are the queries behind the `${prom}`-sourced panels in the library.

**Metric-name mapping.** OpenTelemetry instrument names are dotted; the Prometheus exporter sanitizes them (`.` → `_`), appends `_total` to counters, and appends the unit + `_bucket`/`_sum`/`_count` to histograms. So the C# instruments map like this:

| OTel instrument (C#) | Kind | Prometheus series |
|---|---|---|
| `contact_center.call.started` | counter | `contact_center_call_started_total` |
| `contact_center.call.faulted` | counter | `contact_center_call_faulted_total` |
| `contact_center.call.active` | up-down | `contact_center_call_active` (gauge) |
| `contact_center.call.time_to_first_audio` (ms) | histogram | `contact_center_call_time_to_first_audio_milliseconds_{bucket,sum,count}` |
| `contact_center.edge.dispatch.latency` (ms) | histogram | `contact_center_edge_dispatch_latency_milliseconds_{bucket,sum,count}` |
| `contact_center.strategy.tier_degradations` | counter | `contact_center_strategy_tier_degradations_total` |
| `contact_center.quality.alerts.raised` / `.resolved` | counter | `contact_center_quality_alerts_raised_total` / `..._resolved_total` |
| `gen_ai.client.token.usage` (tokens) | histogram | `gen_ai_client_token_usage_tokens_{bucket,sum,count}` |
| `gen_ai.client.operation.duration` (s) | histogram | `gen_ai_client_operation_duration_seconds_{bucket,sum,count}` |

> The exact `_total`/unit suffixes depend on the collector/exporter config. If your series differ, adjust the `expr` in the affected panel files under `Dashboards/panels/` — the naming lives in one place per panel.

**Call-connect success rate** (the `percentunit` stat):

```promql
sum(increase(contact_center_call_started_total[$__range]))
/ clamp_min(
    sum(increase(contact_center_call_started_total[$__range]))
  + sum(increase(contact_center_call_faulted_total[$__range])), 1)
```

**Active calls & throughput:**

```promql
sum(contact_center_call_active)
sum(rate(contact_center_call_created_total[$__rate_interval]))
sum(rate(contact_center_call_ended_total[$__rate_interval]))
```

**Latency percentiles** (time-to-first-audio; same shape for edge dispatch and LLM operation duration):

```promql
histogram_quantile(0.95,
  sum(rate(contact_center_call_time_to_first_audio_milliseconds_bucket[$__rate_interval])) by (le))
```

**LLM token usage** by type:

```promql
sum(rate(gen_ai_client_token_usage_tokens_sum[$__rate_interval])) by (gen_ai_token_type)
```

**Tier degradations** (graceful fallback) by transition:

```promql
sum(rate(contact_center_strategy_tier_degradations_total[$__rate_interval])) by (from_tier, to_tier)
```

---

## Dynamics 365 conversation diagnostics

Inspect the routing timeline for one call — this is the raw-KQL path that covers transfer/consult (which the Diagnose dashboard does not). Set `contextId` to the call's `cc_contextId`.

```kusto
declare query_parameters(contextId:string = "");
AppTraces
| extend cd = parse_json(Properties)
| extend ConversationId    = tostring(cd["powerplatform.analytics.resource.id"]),
         Subscenario       = tostring(cd["powerplatform.analytics.subscenario"]),
         OmnichannelCallId = tostring(cd["omnichannel.call.id"]),
         ContextId         = tostring(cd["cc_contextId"])
| where ContextId == contextId and isnotempty(contextId)
| project TimeGenerated, ConversationId, Subscenario, OmnichannelCallId, ContextId, Message
| order by TimeGenerated asc
```

---

## Confidence-scored fallback (when a deterministic id is missing)

If an edge is missing a deterministic id (e.g. the Teams leg, Phase 2), score a probable match instead of a hard join:

- `+40` `context_id` matches a D365 context variable
- `+25` ACS `ServerCallId` maps to an IVR span
- `+15` called DID + hashed ANI match
- `+10` start times within tolerance
- `+10` duration / transfer sequence match

Treat anything below your threshold as **probabilistic** and label it as such in the UI.
