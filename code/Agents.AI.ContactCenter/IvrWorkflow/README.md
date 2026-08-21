# IVR Workflow Framework

A declarative, YAML-authored IVR (interactive voice response) framework that compiles
to the existing `Agents.AI.ContactCenter` call runtime and projects into the
[Microsoft Agent Framework Workflows](https://learn.microsoft.com/azure/ai-services/agent-framework/)
graph model.

A single YAML document describes:

- **what the IVR can do** — *capabilities* (balance lookup, card activation, …)
- **how the caller signals intent** — *DTMF*, *NLU*, or a *realtime* agent
- **how the call advances** — *stages* with explicit `transitions`, `onExit`, or
  per-intent / per-digit routing

The same authoring surface drives all interaction strategies, so a workflow can be
"realtime first, degrade to DTMF" without rewriting the flow.

### DTMF input is available to every tier

`scripted.dtmf` is no longer the exclusive province of the dedicated DTMF strategies.
Both the **Realtime AI** strategy and the **NLU** strategy also consume inbound DTMF
tones during normal operation:

- **Realtime AI** — if the active stage has a `scripted.dtmf` block, digits are handled
  deterministically (menu transition or buffered `collect` → validator). Otherwise the
  digit is forwarded to the LLM as an inline user turn (`[Caller pressed 1]`) so the
  model can react conversationally. This covers cases like "caller cannot speak right
  now" and "caller needs to enter a code mid-conversation".
- **NLU** — digits act as a direct intent shortcut. A press resolves through the same
  `scripted.dtmf.options` / `scripted.nlu.intents` table the speech classifier uses, so
  noisy lines or unrecognized accents still have a deterministic escape hatch.
  No tier swap required; the composite fallback (NLU → DTMF tier) still handles repeated
  no-match events at the orchestration layer.

> The full YAML reference lives at [`Schema/Schema.md`](./Schema/Schema.md). This
> document focuses on **how the pieces fit together** and how to consume the framework
> from a host application.

---

## 1. End-to-end architecture

```
  YAML file (Samples/*.yaml)
        │
        ▼
  ┌────────────────────────────┐
  │ CallWorkflowYamlReader    │   reads and validates YAML, produces <name,version> key
  └────────────────────────────┘
        │
        ▼
  ┌────────────────────────────┐
  │ WorkflowGraphCompiler      │   compiles all workflows in a catalog
  │  + IIvrToolRegistry         │   resolves tools, guards, predicates;
  │  + IIvrGuardFactory         │   produces a metadata-only catalog
  │  + IIvrPredicateRegistry    │
  └────────────────────────────┘
        │
  ┌─────┴──────────────────────────┐
  │ ICallWorkflowCatalog          │   singleton workflow metadata by name+version
  └────────────────────────────────┘
        │
        ▼
  ┌────────────────────────────┐
  │ WorkflowStartupValidationService │   validates startup Graphs
  └────────────────────────────┘

```
Every hosted workflow goes through this pipeline at least once:

- **YAML -> CallWorkflowYamlReader** — read and validate the document, produce
  a consistent `(name,version)` key.
- **WorkflowGraphCompiler** — compile all workflows in the catalog:
  - **Tools, Guards, Predicates** — references are resolved and validated
    against the configured `IIvrToolRegistry`, `IIvrGuardFactory`, and
    `IIvrPredicateRegistry`.
  - **Metadata-only catalog** — produces a singleton `ICallWorkflowCatalog`
    instance per unique workflow key.
- **WorkflowStartupValidationService** — temporary scope validates the startup
  Graph structure, transition targets, named predicates, and tool resolutions.
  This service runs once per application lifetime.

Holistic validation ensures that any given `(name,version)` workflow can be located,
is structurally sound, and has all its tools and predicates available before any calls
are processed.

### 1.1 CompiledStage

At the core of the compilation output is a `CompiledStage`, which contains:
- **Stage shape** — id, type, transitions, etc.
- **Tool references** — names of tools to invoke, if any
- **Predicate references** — names of predicates to evaluate, if any

A `CompiledStage` never directly captures or retains any scoped service instances.

---

## 2. Authoring a workflow

A workflow is a single YAML document. The minimal shape is:

```yaml
name: utility-bill-pay
version: 1
description: A DTMF-only bill-payment flow.
strategy:
  primary: dtmf
stages:
  - id: menu
    scripted:
      dtmf:
        ssmlPrompt: "Press 1 to pay your bill, press 2 to check your balance."
        options:
          - { digit: '1', label: PayBill, nextStage: collect-account }
          - { digit: '2', label: Balance, nextStage: collect-account }
  - id: collect-account
    scripted:
      dtmf:
        ssmlPrompt: "Enter your 8-digit account number, followed by pound."
        collect:
          minDigits: 8
          maxDigits: 8
          validator: verify-account-number
          onValidNextStage: confirm
  - id: confirm
    scripted:
      dtmf:
        ssmlPrompt: "Press 1 to confirm, press 2 to start over."
        options:
          - { digit: '1', label: Confirm, nextStage: complete }
          - { digit: '2', label: Restart, nextStage: menu }
  - id: complete
    terminal: true
```

For the full schema (strategy tiers, capabilities, guards, NLU intents, realtime prompt
shape, etc.) see [`Schema/Schema.md`](./Schema/Schema.md). Working samples live in
[`Samples/`](./Samples/):

- `utility-bill-pay.yaml` — pure DTMF, four stages, terminal completion
- `banking-main.yaml` — mixed `realtime + nlu + dtmf` strategy with capabilities,
  guards, identity verification, and a `wrap-up` terminal stage

---

## 3. Hosting the framework

Register the framework in your host's DI container with
`builder.AddStandardContactCenter()...`:

```csharp
builder.AddStandardContactCenter()
       .AddWorkflowsFromDirectory(...)
       .ConfigureDefaultWorkflow(...)
       .AddTools<TTools>()
       .UseStandardVoiceFallback(...);
```

### 3.1. Request WorkflowId precedence

In the above call chain, `ConfigureDefaultWorkflow` establishes a fallback for any
workflow that does not have an explicit `name` / `version` in the request.

However, any `CallWorkflowOptions.DefaultWorkflowId` still applies, and takes
precedence over the default configured in the DI container.

### 3.2. Service lifetimes

| Service                      | Lifetime | Purpose                                                     |
| ---------------------------- | -------- | ----------------------------------------------------------- |
| `WorkflowGraphCompiler`     | Singleton| Compiles all workflows in the catalog                       |
| `ICallWorkflowCatalog`      | Singleton| Resolves workflow metadata by name+version                 |
| `WorkflowRuntimeBinder`     | Scoped   | Binds and activates request-scoped workflow execution       |
| `IIvrToolRegistry`          | Scoped   | Resolves tool names from YAML to `AITool` instances        |
| `INamedEdgePredicateProvider`| Scoped   | Resolves named predicates for `requires:` guards            |
| `CallWorkflowSession`       | Scoped   | The active workflow instance for a call                    |

### 3.3. Startup guarantees

Before any requests are processed, the following validations happen exactly once
per application lifetime:

- **Graph structure** — all workflows defined in YAML are acyclic, and have
  a single, reachable start stage
- **Transition targets** — every transition or route target is a valid stage id
- **Named predicates** — all referenced predicates are registered
- **Missing tools** — every tool reference is resolvable through the
  `IIvrToolRegistry`
- **Duplicate tool names** — no two tools have the same effective name

These validations ensure that any workflow metadata resolved at runtime is backed
by a structurally sound and complete definition.

### 3.4 Runtime behavior

A typical execution flows through the following key stages:

- **Request begins** — `WorkflowRuntimeBinder` activates a `CallWorkflowSession`
  scoped to the incoming request. The binder locates the requested workflow's
  metadata and validates the initial state.
- **Graph execution** — as the graph executes, stages may emit events that cause
  NLU or DTMF input to be processed. This input is routed according to the
  configured workflow, including any composite failover to other input types.
- **State management** — the `CallStateProjector` captures and restores state
  across tier transitions, ensuring a seamless handoff between realtime, NLU,
  DTMF, and composite stages.

This architecture allows for high flexibility in authoring workflows, while
maintaining strict validation and routing guarantees.

---

## 4. The Agent Framework workflow bridge

Removed in favor of direct integration with the Microsoft Agent Framework via
`WorkflowGraphCompiler`.

---

## 5. Validation guarantees

Removed; validation is now handled holistically at startup, and per-request
validation is not permitted to retain state or scoped instances.

---

## 6. Testing

The reference tests live in
[`test/Agents.AI.ContactCenter.Tests/IvrWorkflow/Workflows/IvrWorkflowGraphBuilderTests.cs`](../../../../test/Agents.AI.ContactCenter.Tests/IvrWorkflow/Workflows/IvrWorkflowGraphBuilderTests.cs):

| Test | Validates |
| ---- | --------- |
| `Build_BankingMain_ProducesNodePerStageAndKnownEdges` | One executor per stage, expected edge sources, terminal `wrap-up` has no outgoing edges. |
| `Build_UtilityBillPay_ProducesLinearChainAndTerminalStage` | DTMF transitions become edges, multi-option dedupe (`menu` → one edge to `collect-account` despite two digits), `confirm` has two distinct targets. |
| `Build_MissingTransitionTarget_ThrowsTypedException` | Unknown transition target throws `IvrWorkflowGraphBuildException`. |
| `BuildGraphAsync_LoaderExtension_ProducesSameGraph` | End-to-end DI: loader + graph builder produce the expected `Workflow`. |

These also act as canonical examples of stubbing tools in tests via
`AIFunctionFactory.Create(..., new AIFunctionFactoryOptions { Name = ... })`.

---

## 7. Where to look next

- **YAML reference:** [`Schema/Schema.md`](./Schema/Schema.md) and
  [`Schema/ivr-workflow.schema.json`](./Schema/ivr-workflow.schema.json)
- **Working samples:** [`Samples/banking-main.yaml`](./Samples/banking-main.yaml),
  [`Samples/utility-bill-pay.yaml`](./Samples/utility-bill-pay.yaml)
- **DI surface:** [`DependencyInjection/IvrWorkflowServiceCollectionExtensions.cs`](./DependencyInjection/IvrWorkflowServiceCollectionExtensions.cs)
- **Compiler:** [`Compilation/IvrWorkflowCompiler.cs`](./Compilation/IvrWorkflowCompiler.cs)
