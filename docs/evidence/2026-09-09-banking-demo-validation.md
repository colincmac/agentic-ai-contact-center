# Evidence record: banking demo host validation

- **Date:** 2026-09-09
- **Related ADRs:** [ACS control plane](../adr/0002-acs-call-automation-as-control-plane.md),
  [Event Grid ingress](../adr/0003-incomingcall-delivery-via-event-grid.md),
  [degradation](../adr/0008-graceful-degradation-realtime-to-dtmf.md),
  [host-neutral contracts and verification](../adr/0016-host-neutral-call-contracts-and-verification.md)
- **Class:** measured

## Classification

Measured local functional results from the banking-demo implementation working
tree. These are observed test and preview-server outcomes, not modeled service
capacity or evidence of successful live calls.

## Method

- Compiled the new ASP.NET Core host and ran the affected contact-center test
  project, followed by the scenario test project.
- The [13 host integration cases](../../code/test/Agents.AI.ContactCenter.Tests/DemoHost/BankingDemoHostTests.cs)
  use ASP.NET TestServer HTTP and a WebSocket upgrade with locally signed test JWTs.
  They execute the actual call session, admission, workflow, OTP store, action
  authorization, state projection, and backend demo banking components.
- ACS call control, SMS sending, speech synthesis, realtime inference, and the
  media edge are controlled fakes. The realtime case invokes the exposed
  `advance` function directly and checks its result and stage configuration.
  It does not exercise a remote model's function-call protocol.
- Started the actual host on loopback with live calling explicitly disabled;
  requested status, development OpenAPI, disabled endpoints, and the removed
  scaffold endpoint. Stopped the process after verification.
- Parsed the demo YAML with the repository's existing YAML package and validated
  it with Ajv's JSON Schema 2020-12 implementation against the library schema.

## Environment

- Local Windows development workstation, .NET SDK 10.0.401, `net10.0`.
- VSTest with the repository's xUnit v3 test projects.
- In-process, single-instance state; no distributed deployment.
- No Azure calls, SMS messages, Voice Live inference, or resource provisioning.
- Dependencies include Call Automation 1.6.0-beta.1, Voice Live 1.1.0-beta.3,
  SMS 1.0.2, and ASP.NET Core JWT/OpenAPI/MVC Testing packages 10.0.12.

## Results

| Check | Observed result |
| --- | --- |
| Contact-center test project | 452 passed, 0 failed, 0 skipped |
| Scenario test project | 6 passed, 0 failed, 0 skipped |
| Combined affected projects | 458 passed |
| `demo-bank@1` schema validation | Passed; 11 stages |
| Preview `GET /demo` | 200; `configuration-preview`, demo data explicitly identified |
| Preview `GET /openapi/v1.json` | 200; status, incoming-call, callback, and media routes present |
| Preview incoming-call POST / media GET | 503; live calling disabled |
| Old `/weatherforecast` | 404 |
| Documentation metadata validator | Passed with `--require-initialized` |
| Changed Markdown local file links | 167 targets resolve across 8 documents; not a check of external URLs or all anchors |

The host cases cover authenticated sender/audience boundaries, harmless
subscription validation, duplicate answer admission, OTP success and retries,
OTP silence, unknown ANI, SMS failure, balance, keypad-confirmed activation,
model-only activation denial, transfer acceptance/failure, callback-driven
goodbye, mismatched callback IDs, ambiguous answer failure with late callback
cleanup, and pre-answer capacity overflow.

The first expanded regression run exposed test configuration contamination from
the referenced API's settings and concurrent mutation of a metrics-test list.
The configuration test now clears external providers; the collector now uses a
concurrent queue. After these fixes, the targeted 71-case run and full runs above
passed. The earlier interrupted/failed runs are not counted as successes.

## Assumptions

- The fake connectors model the expected success/failure boundaries, not actual
  cloud timing, authorization metadata refresh, audio transport, or SMS delivery.
- No external telemetry collector is required for these local results.
- The configured fictional customer and sender records are sufficient to test
  the demo flow, not realistic banking identity proofing.

## Limitations

- No real Teams/TPE ingress, ACS media frames/audio, Voice Live inference, SMS
  delivery, operator call, or end-to-end latency was tested.
- No distributed ownership, replica routing, process-restart recovery, load,
  quota, or regional failover measurements were made.
- OTP is deterministic keypad capture. Speech-based secret collection, biometric
  verification, NLU/SLM fallback, and a human desktop are not part of this demo.
- Account/card state is in memory and changes no real financial account.
- Container builds, native Linux Speech dependencies, AKS/ACA deployment,
  private networking, and live telemetry joins remain unvalidated.
- These tests are not a security audit, compliance certification, or evidence
  that preview provider versions are appropriate for production.

## Reproducibility

From the repository root, after restoring dependencies:

```powershell
dotnet test .\code\test\Agents.AI.ContactCenter.Tests\Agents.AI.ContactCenter.Tests.csproj --no-restore --verbosity minimal
dotnet test .\code\test\Agents.AI.ContactCenter.Testing.Tests\Agents.AI.ContactCenter.Testing.Tests.csproj --no-restore --verbosity minimal
```

To run only the host cases, add
`--filter 'FullyQualifiedName~BankingDemoHostTests'` to the first command.
The [demo guide](../../code/ContactCenter.AIAgent/README.md) and
[HTTP examples](../../code/ContactCenter.AIAgent/ContactCenter.AIAgent.http)
describe preview startup and live prerequisites.

For schema reproduction, parse
[demo-bank.yaml](../../code/ContactCenter.AIAgent/Workflows/demo-bank.yaml) using
`yaml`, and compile
[ivr-workflow.schema.json](../../code/Agents.AI.ContactCenter/IvrWorkflow/Schema/ivr-workflow.schema.json)
using `ajv/dist/2020.js`; these are existing repository dependencies.
