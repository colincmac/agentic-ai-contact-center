# Evidence record: .NET call-runtime implementation validation

- **Date:** 2026-09-09
- **Related ADRs:** [ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md),
  [ADR-0014](../adr/0014-call-state-event-folded-provider-slices.md),
  [ADR-0016](../adr/0016-host-neutral-call-contracts-and-verification.md)
- **Class:** measured

## Classification

Observed local functional test outcomes for the implementation working tree.
No performance, deployment, or caller-assurance certification is implied.

## Method

Build and run both affected .NET test projects with their existing VSTest/xUnit v3
runner and restored dependencies. Earlier targeted runs guided fixes; the final
project-level runs below include those tests and must not be added to their counts.

Also validate all three shipped YAML examples against the updated JSON schema
using the repository's existing Ajv 2020 and YAML packages. Runtime reader/compiler
sample conformance is included in the .NET tests.

## Environment

- Local Windows development workstation; .NET SDK 10.0.401, target `net10.0`.
- Source base: `06da7d0ac2a0ae1258f50962b83d53ed2bf10618`, plus the uncommitted
  implementation patch associated with this record.
- Existing restored package versions; no new package/project was added.
- No Azure resource, live call, live speech inference, or biometric model was used.
- Speech DI construction tests use a credential that rejects token acquisition;
  concurrent speech behavior uses deterministic fakes.

## Results

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `Agents.AI.ContactCenter.Tests` | 439 | 0 | 0 |
| `Agents.AI.ContactCenter.Testing.Tests` | 6 | 0 | 0 |
| **Total, distinct project runs** | **445** | **0** | **0** |

The referenced .NET libraries built successfully. All three shipped YAML examples
passed the schema check.

Coverage includes explicit authentication failure routes, equal-rank method
separation, proof expiry/subject binding, credential event/audio isolation,
atomic OTP consumption, action authorization/idempotency keys, the actual pinned
Agent Framework executor adapter, recorded-DTMF callback sequencing, scope-isolated
NLU streams, startup/fallback admission, bounded output draining, observer overflow,
and delayed/concurrent state persistence with aligned replay watermarks.

## Assumptions

- Backend action implementations honor the supplied idempotency key across restarts.
- Host ingress supplies trusted routing facts and locally timestamped media.
- Production verification providers meet the application's assurance requirements.
- Workflow and endpoint configuration is validated before accepting calls.

## Limitations

- Fake streams and in-memory stores are not real Speech, ACS, Redis, or Cosmos tests.
- Native Speech startup/stop behavior was compiled, not exercised against Azure.
- The discrete Agent Framework adapter is not the separate MAF declarative YAML
  engine, nor a durable-media workflow implementation.
- Recorded DTMF requires host-provided assets and a compatible verb edge. Automatic
  replacement of an active streaming edge is not demonstrated.
- Streaming physical playback/transfer completion remains host-specific. A terminal
  workflow stage does not prove that a goodbye was heard or a transfer was accepted.
- Distributed challenge stores, production action backends, spoken-secret capture,
  a full endpoint host, visual authoring, and VXML migration are not delivered here.
- Python, Aspire cloud wiring, infrastructure, and PowerShell scripts were not changed.
- No load, soak, live failover, model quality, liveness, or spoof-resistance tests ran.

## Reproducibility

With the implementation patch and declared dependencies restored, run from the
repository root:

```powershell
dotnet test '.\code\test\Agents.AI.ContactCenter.Tests\Agents.AI.ContactCenter.Tests.csproj' --no-restore --verbosity minimal
dotnet test '.\code\test\Agents.AI.ContactCenter.Testing.Tests\Agents.AI.ContactCenter.Testing.Tests.csproj' --no-restore --verbosity minimal
```

Record the source commit containing the patch when it is committed. The base
revision alone cannot reproduce these results. The
[historical baseline](2026-09-08-code-adoption-validation.md) records earlier
behavior, including a now-replaced fail-open assertion.
