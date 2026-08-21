# Dashboards

The observability design uses a **library of reusable panels** composed into several purpose-built dashboards, plus the original single-pane E2E model. The panel JSON and manifest described below have not yet been migrated into this repository, so the documented assembly flow is not currently runnable. See [setup.md](setup.md#4-azure-managed-grafana--the-single-pane).

```
Agents.AI.Monitoring/Azure/Dashboards/
  contact-center-e2e.json        # the original single-pane E2E model (hand-authored)
  manifest.json                  # which dashboards exist, and which panels compose them
  panels/                        # the reusable panel library (one file per panel)
    _shared/variables.json       #   template variables injected into every dashboard
    red/  telephony/  voice/  genai/  routing/  strategy/  quality/  intent/  trace/
  assemble-dashboards.ps1  # composes generated/*.json from manifest + panels
  generated/                     # built dashboards — import these into Grafana
```

## The panel-reuse model

Grafana has no native "include this panel here" at the file level, so panels are **defined once and composed**:

- **Each panel is its own file** under `panels/<domain>/panel-*.json`. It carries no `id` and no `gridPos.y` (those are assigned at assembly), references data sources through the shared variables (`${ds}` for Azure Monitor, `${prom}` for Managed Prometheus), and has a `__meta` note describing what it is and which reference archetype it derives from.
- **`manifest.json`** lists the dashboards and, per dashboard, the ordered rows and the panels each row contains (by `ref`, with `gridPos` `h`/`w`/`x`). A single panel `ref` can appear in any number of dashboards — that is the reuse.
- **`assemble-dashboards.ps1`** loads the manifest, pulls each referenced panel from the library, injects the shared variables + annotations, computes `id` and `gridPos.y` by flow layout, and writes `generated/<uid>.json`.

Once the panel library and manifest are migrated, editing a panel once will update every dashboard that references it on the next assembly. For **live** shared editing in Grafana itself, promote a panel to a Grafana **library panel** (Grafana links it by `uid` across dashboards) — the file-based library is the source-controlled equivalent.

For the full authoring reference — panel-file anatomy, naming/visualization conventions, template variables, and the layout rules — see [panel-library.md](panel-library.md).

## Composed dashboards

| Dashboard (`uid`) | Focus | Notable panels |
|---|---|---|
| **Fleet Live Ops** (`cc-fleet-live-ops`) | RED golden signals across the fleet | IVR availability, call-connect success, active calls, request rate/latency, tier degradations, quality alerts |
| **Telephony & ACS** (`cc-telephony-acs`) | Call automation + media | ACS operations & reliability, active calls, time-to-first-audio, edge dispatch latency |
| **GenAI & Realtime** (`cc-genai-llm`) | Model cost/latency | LLM token usage, LLM operation duration, realtime turn latency |
| **Transfer & Routing (D365)** (`cc-transfer-routing`) | Hand-off correctness | transfer success / hand-off gap, D365 subscenario distribution, intent distribution, single-call trace |
| **Platform Health** (`cc-platform-health`) | Resilience | availability, call-connect success, tier degradations, quality alerts |

Panels shared across dashboards (e.g. **call-connect success**, **active calls**, **tier degradations**, **time-to-first-audio**) are defined once and referenced from each. Two data sources back the library: **Azure Monitor** (`${ds}`) for logs/traces (App Insights + ACS Log Analytics + D365) and **Managed Prometheus** (`${prom}`) for the IVR's OpenTelemetry metrics. The PromQL and metric-name mapping is in [queries.md](queries.md#metrics--managed-prometheus-promql).

## The E2E single pane (`contact-center-e2e.json`)

## Variables

| Variable | Type | Set to |
|---|---|---|
| `ds` | data source | the **Azure Monitor** data source |
| `law` | textbox | the Log Analytics workspace resource id |
| `e2e` | textbox | the `e2e.call_id` to trace (leave blank for the fleet views) |

## Panels

| Panel | Type | Source | Question it answers |
|---|---|---|---|
| **Single-Call Trace** | table | Log Analytics + App Insights union | "Show me everything that happened on call `$e2e`, in order, across ACS, the IVR, and D365." |
| **Live Ops — IVR RED** | time series | App Insights `AppRequests` | "Is the IVR healthy right now — rate, errors, p95 latency?" |
| **Transfer Success** | stat | ACS logs vs D365 `Traces` | "Did accepted transfers actually land as D365 conversations? What's the hand-off gap?" |
| **Routing / Queue** | time series | D365 conversation diagnostics | "How are calls distributed across routing stages over time?" |

Each panel's query is documented and copy-pasteable in [queries.md](queries.md).

## Suggested layout

```
┌───────────────────────────────────────────────────────────────┐
│  Single-Call Trace  (e2e.call_id = $e2e)                        │  full width
├───────────────────────────────┬───────────────────────────────┤
│  Live Ops — IVR RED            │  Transfer Success (stat)       │  half / half
├───────────────────────────────┴───────────────────────────────┤
│  Routing / Queue — D365 subscenario distribution               │  full width
└───────────────────────────────────────────────────────────────┘
```

## Recommended alerts

Start with two, both derived from panels above:

1. **Hand-off gap** — alert when `TransfersAccepted - D365ConversationsCreated` stays positive over a rolling window. This is the clearest signal that context is being lost across the IVR → D365 boundary.
2. **IVR error rate / latency** — alert on `ErrorRate` or `P95Ms` breaching your SLO from the Live-Ops query.

## Extending the pane

- **Teams leg (Phase 2):** add a panel over the Microsoft Graph call-records ETL and join probabilistically (called DID + hashed ANI + time window) — see [correlation-model.md](correlation-model.md).
- **Per-workstream / per-queue splits:** add `d365.workstream` / `d365.queue` as template variables and filter the D365 panels.
- **Media quality:** out of scope for the MVP; would source from ACS media summary logs.
