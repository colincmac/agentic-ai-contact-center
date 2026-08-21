# ADR-0014 — Per-call state as event-folded immutable provider slices with a pluggable snapshot store

- **Status:** Accepted
- **Date:** 2026-06-23

## Context

Each in-flight call accumulates mutable state: the caller's evolving identity and authentication audit trail, the current IVR/workflow step and collected slot values, retry counters, the active degradation tier, sentiment signals, and more. That state is read and written by many participants in a single call — the conversation strategy, observers (sentiment, recording, dashboard), per-call AI tools, and the IVR navigator — across multiple threads, since a realtime voice call is continuous and concurrent (audio frames, transcript events, tool invocations, and analysis all fire independently).

The original model represented this as one mutable class per concern (`CallerAuthenticationState`, `IvrWorkflowState`), registered `Scoped` and shared across the call's DI scope, with **every field guarded by a lock**. This had several problems:

- **Lock sprawl.** Each property got its own `lock`/`Lock` body; the two classes even mixed primitives (`ConcurrentDictionary` plus an explicit `Lock`), a sign the concurrency model was ad hoc.
- **Three concerns fused into one object.** Concurrency, persistence, and prompt rendering (`RenderAsPrompt`) all lived on the mutable model.
- **A second source of truth.** The platform already emits a typed `StrategyEvent` stream (`CallerIdentified`, `WorkflowStepEntered`, `FunctionCalled`, `CallerVerificationLevelChanged`, …) and already builds projections from it (the dashboard observer). The locked classes were updated by *direct field pokes in addition to* emitting events — two parallel representations that could drift.
- **Locks don't cross pods.** A lock protects in-process memory only. At hyperscale a call is owned by different pods over its lifetime ([ADR-0011](0011-pod-ownership-and-lease-model.md)); the dialog state must survive pod hand-off, which [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) already mandates storing in Redis. The locked in-memory object had no persistence story of its own.
- **Adding a new kind of state was invasive** — a new locked class, threaded into render call sites and persistence by hand.

[ADR-0004](0004-call-state-in-redis-by-callconnectionid.md) decided *where* coordination state lives (Redis namespaces for ownership, dedup, capacity, and a `state:{callConnectionId}` hash for dialog state). It did not decide *how the per-call application state is represented in process* or how it is mutated safely. This ADR fills that gap and makes the backing store pluggable.

The single most important structural fact: `CallSession.PumpEventsAsync` is already the **single reader** of the strategy event stream (`await foreach … _strategy.Events`), running on one task. That is a natural single-writer point.

## Decision

Represent per-call state as **immutable slices derived by folding the `StrategyEvent` stream through pure reducers**, on a single writer, behind a pluggable snapshot store.

### Slices and providers

- Each kind of state is an **immutable record** (e.g. `AuthSnapshot`, `IvrSnapshot`) — the data only.
- Each slice has a **provider** (`CallStateProvider<TState>` implementing `ICallStateProvider`) that supplies a pure reducer `Apply(TState, StrategyEvent) -> TState`, optional prompt rendering, and JSON (de)serialization. The provider *composes* a `CallSessionState<TState>` accessor (slice key + initializer + serializer) rather than inheriting state — the same shape as agent-framework's `ProviderSessionState<TState>`, adapted to a concurrent rather than turn-serialized writer.
- Providers are registered as an **enumerable** (`AddCallStateProvider<T>`, `TryAddEnumerable`). Adding a new kind of state is one class plus one DI line; no core type changes. Providers must be **independent** — a provider folds only the event stream and never reads another provider's slice. Cross-slice facts travel as derived `StrategyEvent`s.

### Single-writer fold; lock-free reads

- A per-call `CallStateProjector` (registered `Scoped`, shared by the call's DI scope) owns a `CallStateBag` — a string-keyed bag of immutable slices published via atomic reference swaps.
- `CallSession.PumpEventsAsync` calls `projector.Fold(event)` for every event, before the observer fan-out. Because that pump is the **only** consumer of the event stream, the fold is the **only writer**. No per-field locking is required; readers call `projector.Get<TState>()` for a lock-free read of the current immutable snapshot.

### Persistence is a separate, pluggable seam

- `ICallStateStore` persists the projected snapshot — all slices as opaque JSON keyed by slice id, plus a `Version` for optimistic concurrency. Three implementations: **InMemory** (default; dev / single-pod / degraded fallback), **Redis** (hash + Lua compare-and-set on the version field; keys hash-tagged by `{callId}` consistent with [ADR-0004](0004-call-state-in-redis-by-callconnectionid.md)), and **Cosmos** (one document per call, partition `/callId`, ETag concurrency, serializer-agnostic via the stream APIs).
- A per-call `CallStatePersister` keeps persistence **off the fold hot path**: `Fold` enqueues the event on an unbounded single-reader channel; the persister appends to the optional log and writes a **debounced** snapshot (after `SnapshotEveryNEvents` or when the batch drains). This is the deliberate departure from `Persist`-then-apply event-sourcing frameworks — it trades synchronous durability for realtime latency, closer to a Kafka Streams local store + changelog.
- `ICallEventLog` (Redis Streams / Cosmos / in-memory) is an **opt-in** append-only audit/replay log, off by default.

### Hydrate on attach

- `CallSession.StartAsync` calls `HydrateAsync` before the pump starts: load the latest snapshot, restore each slice, start the persister. A pod taking over a call mid-flight resumes in place. `EndAsync` performs a final flush after the pump drains.

### Backward and forward compatibility

- A persisted snapshot that predates a newly added provider leaves that slice at its initial value — slices version independently; there is no global schema to migrate.
- `CallStateProjector` implements `ICallSessionState`, so the existing stage-prompt rendering surface keeps working during migration of the legacy state classes.

## Consequences

- **No per-field locks.** Concurrency safety comes from the single-writer invariant, not from mutexes. The mutable, lock-guarded classes are replaced by pure reducers and immutable snapshots.
- **One source of truth.** State is a deterministic projection of the `StrategyEvent` stream. The same stream feeds observers, the dashboard, and (optionally) the audit log — no parallel hand-maintained copy.
- **Pluggable durability and failover.** The same in-process model persists to InMemory, Redis, or Cosmos behind one interface, and rehydrates on pod hand-off ([ADR-0011](0011-pod-ownership-and-lease-model.md)) and across mid-call tier swaps ([ADR-0008](0008-graceful-degradation-realtime-to-dtmf.md)).
- **Extensible by composition.** New state types are additive (`AddCallStateProvider<T>`), which is the property the previous design lacked.
- **Discipline required.** The model only holds if reducers stay pure, `Fold` is called only from the event pump, and bulk/high-frequency data (raw transcript, audio-emotion history, audio frames) stays **out** of folded slices — those belong in their own append log / ring buffer, referenced by id. The README states these rules.
- **Single-writer is load-bearing.** Correctness depends on `PumpEventsAsync` remaining the sole consumer of the strategy event stream. Any future code that mutates slices outside that pump breaks the lock-free guarantee.
- **Eventual, not transactional, persistence.** Debounced snapshots mean a crash can lose the last sub-`SnapshotEveryNEvents` window of state unless the event log is enabled. This is an explicit latency-vs-durability trade; the event log (opt-in) closes the gap for callers that need it.
- **Defense-in-depth concurrency.** The store's version/ETag check guards the brief ownership hand-off window; it is not the primary writer-safety mechanism (the ownership lease is).
- **Relationship to ADR-0004.** This ADR refines the *representation and mutation discipline* of the `state:{callConnectionId}` dialog state ADR-0004 chose to keep in Redis, and generalizes the backing store. ADR-0004's namespaces for ownership/dedup/capacity are unchanged.

## Alternatives considered

- **Keep the lock-per-field mutable classes.** Rejected. Verbose, error-prone, fuses three concerns, and provides no cross-pod persistence — the core problems above.
- **Immutable snapshot swapped under one lock (no event sourcing).** A real improvement over per-field locks, but still a second source of truth maintained by direct writes, and it discards the already-present typed event stream. Folding events is barely more code and unifies the representation.
- **Adopt agent-framework `ProviderSessionState<TState>` verbatim.** Its composition model and string-keyed JSON `StateBag` are excellent and were adopted. Its **read-modify-write** write path was **not**: it is safe in agent-framework only because agent turns are serialized, whereas a voice call is concurrent multi-writer, so a shared read-modify-write bag would lose updates. We keep the single-writer fold and use the `ProviderSessionState` shape only for the storage/accessor layer.
- **Full event sourcing as the default (event log is the system of record, synchronous append-then-apply).** Rejected as the default for latency: synchronous persistence on every event sits on the realtime hot path. The log is available opt-in; snapshots are the default with debounced writes.
- **Redis-only (no store abstraction).** Rejected. The framework must run in dev/test/single-pod without Redis, and Cosmos is the right home for longer-lived call records. One interface with three implementations keeps the in-process model identical across environments.
- **A single global call-state object instead of independent slices.** Rejected. It reintroduces cross-cutting coupling and makes adding state types invasive; the provider/slice registry (Redux `combineReducers`-style) keeps concerns isolated and additive.

## Implementation update (2026-06)

The design was implemented as decided, with three refinements discovered during the build. They keep every property above; this note supersedes the matching lines in **Decision** / **Consequences**.

- **Folding moved from the event pump to an emit-time funnel.** Per-tier strategies (the workflow executor, inline-auth driver, predicates, and tools) need **read-after-write**: emit an event, then immediately read the resulting state on the same call stack. The single-event-pump-thread writer could not provide that. Folding now happens in a per-call `FoldingChannelWriter` that the strategy wraps around its downstream event writer: it folds into the projector synchronously, then forwards the event to `CallSession.PumpEventsAsync` for observers. The "single writer" invariant is preserved by a **single per-call fold lock** in `CallStateProjector.Fold` (replacing thread-affinity); `Get<T>()` reads stay lock-free. The Consequences notes about `Fold`/`PumpEventsAsync` being the sole writer should be read as "the single fold lock is the sole writer."
- **Persister and accessor were inlined/flattened.** `CallStatePersister` is now a private background loop inside `CallStateProjector`, and `CallSessionState<TState>` was flattened into `CallStateProvider<TState>` — fewer types, same behavior.
- **The legacy classes are gone and the plane is always-on.** `IvrWorkflowState` and `CallerAuthenticationState` were deleted; `AuthSnapshot`/`IvrSnapshot` are the single source of truth read by the executor, predicates, authenticators, and tools. The projector + built-in providers + in-memory store are registered by `AddCallSessionContainer` (via `AddDefaultCallStatePlane`); `AddCallState(...)` only overrides the backend (Redis/Cosmos). The `ICallSessionState` migration shim has served its purpose.
