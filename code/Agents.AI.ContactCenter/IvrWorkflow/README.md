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
  │ IIvrWorkflowDefinitionSource│   pluggable: filesystem, IConfiguration, Azure Blob
  └────────────────────────────┘
        │  IvrWorkflowSourceEntry (raw YAML text)
        ▼
  ┌────────────────────────────┐
  │ IIvrWorkflowLoader          │   parse → validate → compile, with caching
  └────────────────────────────┘
        │  IvrWorkflowDocument (typed YAML model)
        ▼
  ┌────────────────────────────┐
  │ IIvrWorkflowCompiler        │   resolves tools, guards, predicates;
  │  + IIvrToolRegistry         │   produces a runtime-ready bundle
  │  + IIvrGuardFactory         │
  │  + IIvrPredicateRegistry    │
  └────────────────────────────┘
        │  CompiledIvrWorkflow  (Stages + RealtimeIvrWorkflowDefinition runtime)
        ▼
  ┌────────────────────┐   ┌─────────────────────────────┐
  │ Call runtime       │   │ IIvrWorkflowGraphBuilder    │
  │ (existing IVR      │   │ ↓                           │
  │  controller / nav) │   │ Microsoft.Agents.AI.Workflows│
  └────────────────────┘   │  Workflow (executors+edges) │
                           └─────────────────────────────┘
```

Two consumption paths share the same compiled artifact:

1. **Legacy/runtime path** — `CompiledIvrWorkflow.Runtime` exposes a
   `RealtimeIvrWorkflowDefinition` consumed by the existing
   `RealtimeIvrWorkflowController` and `IvrWorkflowNavigator`. No call-handling code
   had to change.
2. **Workflow-graph path** — `IIvrWorkflowGraphBuilder` projects the compiled stages
   into a `Microsoft.Agents.AI.Workflows.Workflow`, enabling visualization,
   orchestration, and reuse alongside any other Agent Framework workflow.

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
`AddIvrWorkflowFramework(...)`:

```csharp
services.AddIvrWorkflowFramework(b =>
{
    // 1. Where do YAML documents come from? (pluggable, can stack multiple sources)
    b.AddFileSystemSource(Path.Combine(AppContext.BaseDirectory, "IvrWorkflow", "Samples"));
    // b.AddConfigurationSource(builder.Configuration, "IvrWorkflows");
    // b.AddBlobSource(containerClient);

    // 2. Tools the workflows reference by name.
    b.AddToolsFromAssembly(typeof(BankingTools).Assembly);   // [McpServerTool] / [AITool] discovery
    b.AddTool("balance-lookup", AIFunctionFactory.Create(BankingTools.GetBalance));
    b.AddTool("activate-card",  AIFunctionFactory.Create(BankingTools.ActivateCard));

    // 3. (Optional) named predicates referenced by `requires: [{ type: predicate, predicate: ... }]`
    b.AddPredicate("is-business-hours", state => DateTime.UtcNow.Hour is >= 13 and < 23);
});
```

`AddIvrWorkflowFramework` registers:

| Service                      | Lifetime | Purpose                                                     |
| ---------------------------- | -------- | ----------------------------------------------------------- |
| `IIvrToolRegistry`           | Singleton| Resolves tool names from YAML to `AITool` instances.        |
| `IIvrPredicateRegistry`      | Singleton| Resolves named predicates for `requires:` guards.           |
| `IIvrGuardFactory`           | Singleton| Builds `IIvrStepGuard` from declarative guard descriptors.  |
| `IIvrWorkflowCompiler`       | Singleton| YAML document → `CompiledIvrWorkflow`.                      |
| `IIvrWorkflowLoader`         | Singleton| Source → parse → validate → compile (with cache).           |
| `IIvrWorkflowGraphBuilder`   | Singleton| `CompiledIvrWorkflow` → `Workflow` (Agent Framework graph). |
| `IIvrWorkflowDefinitionSource` | Singleton (multi) | One per `Add*Source(...)` call.                |

### Loading a workflow at request time

```csharp
public sealed class CallEntryPoint(IIvrWorkflowLoader loader)
{
    public async Task<CompiledIvrWorkflow> LoadAsync(string workflowId, CancellationToken ct)
        => await loader.LoadAsync(workflowId, ct);
}
```

`LoadAsync` is async, cached, and validates the document against
[`ivr-workflow.schema.json`](./Schema/ivr-workflow.schema.json) and the cross-reference
validator before returning.

---

## 4. The Agent Framework workflow bridge

The new bridge in [`Workflows/`](./Workflows/) projects each compiled stage into a
graph node that the Microsoft Agent Framework can execute and visualize.

### Components

| File | Role |
| ---- | ---- |
| `IvrStageMessage.cs` | Record passed between executors. Carries `StageId`, `FromStageId`, optional `NextStageIdHint`, and accumulated `State`. `RouteTo(to, from)` is the canonical way to forward routing decisions. |
| `IvrStageExecutor.cs` | `Executor<IvrStageMessage>` representing one stage. Stamps provenance into the outgoing message and either yields workflow output (terminal stages) or broadcasts the message to connected edges. |
| `IIvrWorkflowGraphBuilder.cs` | Contract: `Workflow Build(CompiledIvrWorkflow)`. Throws `IvrWorkflowGraphBuildException` on invalid graphs (duplicate stage ids, unknown transition targets). |
| `IvrWorkflowGraphBuilder.cs` | The bridge implementation (see below). |
| `IvrWorkflowLoaderGraphExtensions.cs` | `loader.BuildGraphAsync(graphBuilder, workflowId, ct)` — convenience one-shot load + compile + bridge. |

### How transitions are projected

The bridge aggregates transitions from **every authoring surface** the compiler
recognizes, then dedupes by target so each `(source, target)` pair becomes a single
edge:

1. `RuntimeStep.ConversationState.Transitions` — explicit YAML `transitions:`,
   intent `next_stage`, and `on_exit`.
2. `RuntimeStep.StepDtmfConfiguration.MenuOptions[d].NextStepId` — DTMF menu choices.
3. `RuntimeStep.StepDtmfConfiguration.OnValidNextStepId` — DTMF digit-collection
   success branch.

Edge predicates honor `IvrStageMessage.NextStageIdHint`: if the executor set a hint,
only the matching edge fires; if no hint was set **and** the source has exactly one
outgoing edge, the message single-steps automatically. Otherwise the predicate is a
no-op and the host (or a future routing decision) must set the hint.

Terminal stages (`terminal: true`) are wired as workflow outputs via
`WorkflowBuilder.WithOutputFrom(...)`.

### Building and inspecting a graph

```csharp
public sealed class IvrGraphPreview(IIvrWorkflowLoader loader, IIvrWorkflowGraphBuilder graphs)
{
    public async Task<Workflow> PreviewAsync(string id, CancellationToken ct)
    {
        // One-shot: load → compile → project to Workflow
        return await loader.BuildGraphAsync(graphs, id, ct);
    }

    public static void PrintShape(Workflow workflow)
    {
        Console.WriteLine($"Start: {workflow.StartExecutorId}");
        foreach (var (sourceId, edges) in workflow.ReflectEdges())
        {
            foreach (var edge in edges)
            {
                Console.WriteLine($"  {sourceId} → {edge.TargetId}");
            }
        }
    }
}
```

### Driving the graph

`IvrStageExecutor` accepts an `IvrStageMessage` and forwards it. To run a workflow,
seed the start executor with an initial message:

```csharp
var workflow = builder.Build(compiled);
var run = await InProcessExecution.RunAsync(workflow, new IvrStageMessage(
    StageId:        workflow.StartExecutorId,
    FromStageId:    null,
    NextStageIdHint:null,
    State:          ImmutableDictionary<string, object?>.Empty));
```

Each executor sets `FromStageId` to its own id before forwarding, so downstream edge
predicates can decide whether to route. The runtime fan-out is therefore *fully
deterministic* given a `NextStageIdHint`, while still supporting "single outgoing edge
auto-routes" for linear flows that don't need explicit hints.

---

## 5. Validation guarantees

Before the runtime ever sees a workflow, the loader has already:

- validated the YAML against [`ivr-workflow.schema.json`](./Schema/ivr-workflow.schema.json);
- enforced **uniqueness** of stage ids and capability ids;
- verified that every transition target, `on_exit`, intent `next_stage`, capability
  reference, and DTMF `nextStage` resolves to a known stage / capability id;
- resolved every tool name through `IIvrToolRegistry`, failing the compile if a tool
  is missing (so production hosts can't silently no-op).

The graph builder adds two additional structural checks at projection time:

- no duplicate stage ids in the compiled bundle (defense in depth), and
- every aggregated transition resolves to a known executor, throwing
  `IvrWorkflowGraphBuildException` otherwise.

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
- **Graph bridge:** [`Workflows/IvrWorkflowGraphBuilder.cs`](./Workflows/IvrWorkflowGraphBuilder.cs)
- **Tool registry:** [`Registry/IvrToolRegistry.cs`](./Registry/IvrToolRegistry.cs) and
  [`Registry/IvrBuiltInTools.cs`](./Registry/IvrBuiltInTools.cs) (auto-registered
  `transfer-to-human`, `end-session`, `acknowledge`)
