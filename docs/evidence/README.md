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

No evidence records have been migrated into this repository yet. Architecture
capacity figures are modeled targets, and implementation behavior remains
unverified here until the demonstrator and its tests are added under `code/`.

Known gaps include:

- no end-to-end call latency or transfer measurements;
- no concurrent-call or per-pod WebSocket benchmark;
- no multi-cluster failover or region-drain exercise;
- no deployed monitoring validation against real traffic; and
- no repository implementation tests for degradation, ownership leases, or
  folded call-state behavior.

## Creating a record

- [`templates/evidence-record.template.md`](templates/evidence-record.template.md) —
  copy this to create a new evidence record (for example
  `2024-06-load-test.md`, `failover-drill.md`).

## Index

No evidence records are currently available. Add each reviewed record here
when real measurements, drills, models, or explicit assumptions are captured.
