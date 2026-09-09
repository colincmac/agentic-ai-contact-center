# Evidence

This folder holds evidence records for this solution: the measured results,
validated drills, and modeled targets that back claims made in
[architecture](../architecture/README.md) documents, [ADRs](../adr/README.md),
and the [publication brief](../publishing/README.md).

## Ownership

Evidence records are owned and maintained by this repository. They exist to
keep a hard line between what was **measured**, what was **modeled**, and
what is **assumed** — never blur these together, here or in anything
derived from here.

## Current state

The [.NET implementation and tests](../../code/ContactCenter.slnx) are present.
A [runtime implementation validation record](2026-09-09-dotnet-runtime-validation.md)
captures 445 passing tests across the two affected projects. The
[earlier baseline](2026-09-08-code-adoption-validation.md) remains historical.
Neither record validates live telephony, model inference, distributed deployment,
authentication-provider assurance, or scale.
Architecture capacity figures remain modeled targets.

Known gaps include:

- no end-to-end call latency or transfer measurements;
- no concurrent-call or per-pod WebSocket benchmark;
- no multi-cluster failover or region-drain exercise;
- no deployed monitoring validation against real traffic; and
- no demonstrated end-to-end degradation/ownership/state behavior under
  distributed failures. Repository unit tests exist for these components,
  but their existence or success is not a substitute for those drills.

## Creating a record

- [`templates/evidence-record.template.md`](templates/evidence-record.template.md) —
  copy this to create a new evidence record (for example
  `2024-06-load-test.md`, `failover-drill.md`).

## Index

| Record | Class | Scope |
| --- | --- | --- |
| [2026-09-08 code-adoption validation](2026-09-08-code-adoption-validation.md) | `measured` | Focused local .NET unit tests; not performance or end-to-end readiness evidence |
| [2026-09-09 .NET runtime validation](2026-09-09-dotnet-runtime-validation.md) | `measured` | 445 passing tests in the implementation working tree; explicit integration limitations |
