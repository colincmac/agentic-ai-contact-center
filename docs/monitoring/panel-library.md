# Panel Library & Conventions

The Grafana assets are a **library of reusable panels** composed into dashboards by a build script, rather than a set of hand-maintained monolithic dashboard JSON files. This is the same split/assemble model the reference contact-center dashboard used, inverted: instead of exploding one dashboard into panels, we compose *many* dashboards from *one* panel library.

This document is the authoring reference — the folder layout, the anatomy of a panel file, the naming/visualization conventions, and how to add a panel or a dashboard. For the list of shipped dashboards and the high-level reuse model, see [dashboards.md](dashboards.md); for the queries behind the panels, see [queries.md](queries.md).

Everything lives under `Agents.AI.Monitoring/Azure/Dashboards/`:

```
Dashboards/
  contact-center-e2e.json        # the original single-pane E2E model (hand-authored, standalone)
  manifest.json                  # dashboards + which panels compose them
  panels/                        # the reusable panel library — one file per panel
    _shared/variables.json       #   template variables injected into every dashboard
    red/  telephony/  voice/  genai/  routing/  strategy/  quality/  intent/  trace/
  build/assemble-dashboards.ps1  # composes generated/*.json from manifest + panels
  generated/                     # built dashboards — import THESE into Grafana
```

---

## Anatomy of a panel file

A panel file is a single Grafana panel object with three deliberate omissions — `id` and `gridPos.y` are assigned at assembly, and a `__meta` note documents provenance. Example (`panels/telephony/panel-call-connect-success-rate.json`, abbreviated):

```jsonc
{
  "__meta": "REUSABLE. Call-connect success rate from Managed Prometheus … the success-rate-stat archetype from the reference dashboard, retargeted at our CallingTelemetry counters.",
  "type": "stat",
  "title": "Call-connect success rate",
  "datasource": { "type": "prometheus", "uid": "${prom}" },
  "fieldConfig": { "defaults": { "unit": "percentunit", "thresholds": { "steps": [ /* red / yellow@0.95 / green@0.99 */ ] } } },
  "options": { "reduceOptions": { "calcs": ["lastNotNull"] } },
  "targets": [ { "refId": "A", "datasource": { "type": "prometheus", "uid": "${prom}" }, "expr": "…", "range": true } ]
}
```

Rules for every panel file:

| Rule | Why |
|---|---|
| **No `id`** | The assembler assigns dashboard-unique ids. A hard-coded id would collide when the panel appears in multiple dashboards. |
| **`gridPos` carries only `h`/`w`/`x` — never `y`** | `y` is computed by the assembler's flow layout. In the manifest you set `h`/`w`/`x`; the panel file itself needn't carry `gridPos` at all. |
| **Reference data sources only through variables** | `${ds}` (Azure Monitor) or `${prom}` (Managed Prometheus). Never hard-code a datasource `uid` — that's what makes a panel portable across dashboards and environments. |
| **Include a `__meta` note** | One line: what the panel is and which reference archetype it derives from. The assembler strips it on build, so it never reaches Grafana. |
| **Put the datasource ref on both the panel and every target** | Grafana expects it in both places. |

---

## Folder & naming conventions

- **One panel per file**, named `panel-<slug>.json`, grouped into a **domain folder**:

  | Folder | Domain | Backing source |
  |---|---|---|
  | `red/` | IVR golden signals (rate/errors/duration) | Azure Monitor Logs (`AppRequests`) |
  | `telephony/` | ACS call automation, connect, concurrency | ACS Log Analytics + Managed Prometheus |
  | `voice/` | Realtime media latency (TTFA, dispatch) | Managed Prometheus histograms |
  | `genai/` | LLM cost & latency | Managed Prometheus (GenAI OTel metrics) |
  | `routing/` | Transfer & D365 routing | Azure Monitor Logs (ACS + D365 Traces) |
  | `intent/` | Detected-intent distribution | Azure Monitor Logs (IVR `AppTraces`) |
  | `strategy/` | Tier degradation / graceful fallback | Managed Prometheus |
  | `quality/` | Call-quality alerts | Managed Prometheus |
  | `trace/` | Single-call drill-down | Azure Monitor Logs (union) |

- **Panel `title`** is human-readable and self-contained (it may appear on any dashboard): `Call-connect success rate`, `Time to first audio (p50/p95/p99)`.
- **Row `title`** (defined in the manifest, not a file) uses the `Section` form (`Golden signals (RED)`, `ACS Call Automation`). The reference dashboard's `Section | Subsection` convention is equally acceptable for denser dashboards.

---

## Data-source conventions

Two data sources back the whole library; both are template variables so a panel never pins an environment-specific `uid`:

| Variable | Grafana data source | Use for |
|---|---|---|
| `${ds}` | Azure Monitor (`grafana-azure-monitor-datasource`) | Logs & traces — App Insights (`AppRequests`/`AppTraces`/`AppDependencies`), ACS Log Analytics (`ACSCallAutomationIncomingOperations`), D365 conversation diagnostics. |
| `${prom}` | Managed Prometheus (`prometheus`) | Metrics — the IVR's OpenTelemetry meters (`CallingTelemetry`, GenAI session). |

Query macros to use (never hard-code a time window):

- **Azure Monitor Logs (KQL):** `| where TimeGenerated $__timeFilter()`; interpolate dashboard variables directly (e.g. `declare query_parameters(e2e:string = '${e2e}')`).
- **Prometheus (PromQL):** `$__rate_interval` for `rate()`, `$__range` for window totals.

The full metric-name mapping (OTel dotted → Prometheus sanitized) and copy-pasteable PromQL/KQL live in [queries.md](queries.md#metrics--managed-prometheus-promql).

---

## Visualization conventions

These are lifted from the reference dashboard's panel archetypes and applied consistently so panels read the same across dashboards.

| Archetype | `type` | `unit` | Thresholds / legend | Example |
|---|---|---|---|---|
| **Reliability / availability** | `stat` or `timeseries` | `percentunit`, `decimals: 3` | thresholds `red → yellow@0.95 → green@0.99`; timeseries uses `thresholdsStyle: area`; stat `colorMode: value`, `noValue: "100%"` | `red/panel-ivr-availability-stat`, `telephony/panel-acs-operation-reliability` |
| **Latency percentiles** | `timeseries` | `ms` (or `s` for GenAI) | separate p50/p95/p99 series; legend `table` right with `min/mean/max/lastNotNull` | `voice/panel-time-to-first-audio`, `genai/panel-llm-operation-duration` |
| **Volume / throughput** | `timeseries` | `none` | `drawStyle: bars`, `fillOpacity: 25`, legend `sum`; stack when it's a composition (tokens, subscenarios) | `genai/panel-llm-token-usage`, `routing/panel-d365-subscenario` |
| **Success-rate KPI** | `stat` | `percentunit` | two targets + math, `colorMode: value` | `telephony/panel-call-connect-success-rate` |
| **Categorical distribution** | `piechart` | `none` | donut, legend `table` with `value`/`percent` | `intent/panel-intent-distribution` |
| **Drill-down / timeline** | `table` | — | `filterable: true`, sort by time | `trace/panel-single-call-trace` |

When in doubt, copy the closest existing panel in the same domain folder and adjust the query + title.

---

## Template variables

Injected into every generated dashboard from `panels/_shared/variables.json`. Any panel can rely on these existing:

| Variable | Type | Meaning |
|---|---|---|
| `ds` | datasource | Azure Monitor data source (logs/traces/metrics). |
| `prom` | datasource | Managed Prometheus data source (IVR metrics). |
| `law` | textbox | Log Analytics workspace resource id (target of KQL panels). |
| `e2e` | textbox | Canonical `e2e.call_id` to trace; blank for fleet views. |
| `ns` | textbox | Kubernetes namespace/regex for resource panels; blank matches all. |
| `org` | textbox | Optional organization/workstream filter; blank matches all. |

To add a variable that only one dashboard needs, put it on that dashboard's `extraVars` array in the manifest — the assembler appends it to the shared list.

---

## Adding a panel

1. Create `panels/<domain>/panel-<slug>.json` following the anatomy + conventions above. Start from the nearest existing panel.
2. Reference it from one or more dashboards in `manifest.json` under a row's `panels`, giving `ref` (path under `panels/` without `.json`) and `gridPos` (`h`/`w`/`x` only):

   ```jsonc
   { "ref": "telephony/panel-call-connect-success-rate", "gridPos": { "h": 6, "w": 6, "x": 0 } }
   ```

3. Rebuild (below). The same `ref` may appear in any number of dashboards — that is reuse.

### Layout rules

The assembler flows panels left-to-right within a row and computes `gridPos.y`:

- Columns are the standard Grafana 24-unit grid; `x + w` should stay ≤ 24 on a visual line.
- A panel with **`x: 0` (after the first panel in the row) starts a new visual line** — that's how the assembler detects wrapping.
- Each row becomes a Grafana `row` panel; its title comes from the manifest.

---

## Building & regenerating

The required `manifest.json`, panel library, shared variables, and generated
dashboard assets have not yet been migrated into this repository. Once they
are present, [`scripts/monitoring/assemble_dashboards.ps1`](../../scripts/monitoring/assemble_dashboards.ps1)
will read the manifest, load each referenced panel, inject shared variables and
annotations, assign layout, and write `generated/<uid>.json`.

The composed dashboards are embedded as deployable assets (`Agents.AI.Monitoring.csproj` globs `Azure\Dashboards\**\*.json`), so re-run the assembler and rebuild the project when panels change.

---

## Source-controlled library vs. Grafana library panels

This file-based library is the **source-controlled** equivalent of Grafana **library panels**:

- **File library (here):** panels are versioned in git, reviewed in PRs, and materialized into dashboards at build time. Best for repeatable, environment-agnostic provisioning.
- **Grafana library panels:** Grafana stores a panel once and links it by `uid` across dashboards, so edits in the Grafana UI propagate live. Promote a hot panel to a library panel when operators need to tune it in place without a rebuild.

The two coexist: keep the canonical definition in the file library, and promote specific panels to Grafana library panels where live editing matters.
