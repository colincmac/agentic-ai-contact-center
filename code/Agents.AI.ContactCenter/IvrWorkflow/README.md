# Call workflows

The accelerator owns call-control semantics; Agent Framework supplies agent and
workflow integration. Live audio and sockets remain outside durable workflow state.
No Orleans runtime or new project is required.

## Contracts

| Concern | Configuration / API |
| --- | --- |
| Trusted called-identity routing | [CallIngressOptions / CallIngressRouter](../Configuration/CallIngressOptions.cs) |
| Versioned business flow | [WorkflowBlueprint](Blueprint/WorkflowBlueprint.cs), [YAML reader](Loading/CallWorkflowYamlReader.cs) |
| Enabled interaction profiles and capacities | [CallInteractionOptions](../Configuration/CallInteractionOptions.cs), [profile registration](../DependencyInjection/CallInteractionServiceCollectionExtensions.cs) |
| Required caller evidence and failure behavior | [AuthenticationPlanBlueprint](Blueprint/AuthenticationPlanBlueprint.cs), [caller authentication](../Authentication/README.md) |

The [JSON schema](Schema/ivr-workflow.schema.json) and [field reference](Schema/Schema.md)
describe the current dialect. `name`, `strategy`, `capabilities`, and nested
`scripted.dtmf` from older samples are not accepted. Unknown and duplicate YAML
fields fail parsing. Missing directories fail loading.

## Execution

The compiler checks stage IDs, transition labels/targets, menu and intent mappings,
authentication failure routes, and action outcome routes. Runtime binding validates
registered tools, credential authenticators, actions, and named interaction profiles
in a temporary startup scope. Call-specific tool bindings are recreated in the call scope.

The catalog accepts multiple revisions and resolves `workflow-id@version`. A bare ID
works only when it identifies one revision. The selected revision is recorded in the
IVR snapshot. Resuming with a different revision fails rather than silently changing
an in-flight call's business process.

[WorkflowExecutor](Execution/WorkflowExecutor.cs) serializes control operations using
a gate shared by executors in the call scope. Stale transition edges and credential
submissions outside the active authentication group are rejected. Successful named
methods are checked by subject and age, not substituted by an unrelated method's
numeric assurance rank.

## Business actions

A stage can declare `action`, `onActionSuccess`, and `onActionFailure`. Register the
named [ICallWorkflowAction](Execution/CallActionDispatcher.cs) in the call scope.
After required authentication succeeds, the same action runs regardless of whether
DTMF, NLU, or realtime led the caller to that stage.

The dispatcher calls ASP.NET Core resource-based authorization before execution.
It supplies validated workflow data, caller identity, and a stable idempotency key.
Repeated requests for the same action stage share one in-process result.
**The backend must honor that key across process failures.** This is not a
transactional outbox or a guarantee of exactly-once external side effects.
Return a failed result for a known rejected operation; surface an uncertain outcome
without claiming success. Do not put two distinct transactions on repeated visits
to the same action stage: this version executes an action stage once per call revision.

Realtime `AIFunction` bindings additionally enforce the active stage and caller
evidence before invoking the business function. Existing tool-specific approval
attributes remain enforced by the realtime approval middleware. Only explicit
`WorkflowDataRecorded` events update collected data; tool arguments are not
automatically persisted as validated slots.

## Agent Framework integration

[CallWorkflowCommandExecutor](AgentFramework/CallWorkflowCommandExecutor.cs) is a real
`Microsoft.Agents.AI.Workflows.Executor` using the existing 1.2.0 package. It handles
discrete entry/transition commands and returns typed outcomes. The host retains the
call-scoped runtime; MAF checkpoints do not serialize its live service scope or media session.
The [compatibility test](../../test/Agents.AI.ContactCenter.Tests/IvrWorkflow/Execution/AgentFrameworkAdapterTests.cs)
executes the adapter through `InProcessExecution`.

This is not an implementation of Agent Framework's separate declarative YAML dialect.
The inspected upstream declarative builder exposes agent, HTTP, and MCP handlers;
an IVR-specific declarative action/plugin path has not been proven here. No preview
dependency was added speculatively. Keep the adapter boundary until those capabilities
can be validated without placing call-long media loops inside workflow steps.

## Interaction and failure

Register realtime, NLU, synthesized DTMF, or recorded DTMF strategies explicitly.
Profiles map customer names to registered strategies and explicit capacity budgets.
`interactionProfiles` restricts a stage to named eligible profiles; provider health
and capacity admission remain separate checks.

Bind the host's `CallInteraction` section with `AddCallInteractionProfiles`.
Profiles are ordered by degradation priority and currently map one-to-one to
registered tier keys. Each enabled profile needs a positive `MaxConcurrent`;
derive it from approved capacity, not the old modeled defaults. Unconfigured
low-level tier capacities do not admit calls. Realtime/NLU/DTMF are the default
implemented order; chat/SLM tiers require a supplied implementation.

The standard facade installs a composite for every entry tier, so a call admitted
directly to NLU can still degrade to DTMF. Initial backend failure and mid-call
failure both move the admission reservation. A disabled mid-call degradation
policy ends the failed strategy instead of silently activating another tier.
Retired producer output is drained before a stop-playback command and activation
of its replacement. Physical media already delivered cannot be undone.

## State and observer bounds

[CallStateProjector](../State/CallStateProjector.cs) folds live state immediately
and persists the same ordered stream through a separate durable-prefix projection.
Snapshots correspond to that prefix's event watermark. `FlushAsync` is an ordered
barrier after hydration, not an external business-transaction commit.

Configure `CallStateOptions.PersistenceQueueCapacity`, `SnapshotEveryNEvents`,
and `ShutdownTimeout` for the workload. Overflow/storage failures fault persistence;
the call session observes this even when no further event arrives.
`ObserverQueueCapacity` bounds each observer feed. Optional observers can lose
events with an overflow warning; `RequiresLosslessDelivery` observers instead
cause the call to fail on overflow. These bounds are not measured capacity claims.

The [recorded DTMF adapter](../Calling/Strategies/Dtmf/RecordedDtmfCallWorkflowStrategy.cs)
requires an ACS verb-capable edge, callback signals, call control, recorded HTTPS
prompt assets, and an `onInputFailure` route for interactive stages. It waits for
`PlayCompleted` before recognition or terminal hangup and correlates operation IDs.
It has no live Speech/model dependency. It routes required authentication to the
explicit authentication failure stage because no recorded credential-prompt adapter
is supplied in this version.

Register it with `AddRecordedDtmfCallWorkflowStrategy` instead of the streaming DTMF
registration. An existing streaming socket cannot carry ACS file/recognition verbs:
the host must explicitly replace the edge or route overflow. The composite must not
claim a successful downgrade to a capability-incompatible edge.

Synthesized DTMF and NLU use the shared numeric credential collector. Realtime uses
that collector too, with trusted synthesized prompts rather than an LLM credential
tool. When capture is unavailable, the authored failure route is taken.

`terminal` means logical workflow completion, including initial and failure stages.
The recorded adapter waits for its final playback callback before hanging up.
Streaming hosts must coordinate their physical playback/transfer completion before
hangup; the executor does not cancel a realtime response immediately on entering a
terminal stage. No generic playback-completed acknowledgement is invented here.

## Speech session ownership

[AddAzureSpeech](../DependencyInjection/AzureSpeechServiceCollectionExtensions.cs)
registers one stateful resilient recognizer per call scope and a shared synthesis
pipeline. The [NLU registration](../DependencyInjection/CallWorkflowStrategyExtensions.cs)
uses a call-scoped agent, including its keyed Agent Framework alias, so one call
cannot complete another call's recognizer. Resolve these services from a call scope,
not the root provider.

The Azure recognizer creates one push stream/session lazily and awaits real SDK
startup rather than a fixed delay. Its start/stop waits use the configured Speech
attempt timeout. The unused recognizer preallocation pool and synchronous synthesis
network warm-up are removed. The endpoint registry owns shared service disposal.
These are lifecycle guarantees, not measured latency or throughput improvements.

## Samples and validation

- [banking-main.yaml](Samples/banking-main.yaml): navigation across modalities, no financial operations.
- [utility-bill-pay.yaml](Samples/utility-bill-pay.yaml): DTMF navigation; no payment is claimed.
- [authenticated-realtime-bank.callworkflow.yaml](Samples/authenticated-realtime-bank.callworkflow.yaml):
  authentication-plan example requiring host-provided authenticators/tools.
- [Workflow tests](../../test/Agents.AI.ContactCenter.Tests/IvrWorkflow/) and
  [recorded DTMF test](../../test/Agents.AI.ContactCenter.Tests/Calling/RecordedDtmfTests.cs).

The examples are not connected banking systems. No visual editor, VXML importer,
Python changes, new projects, or Aspire cloud/resource configuration are included.
The [solution documentation](../../../docs/README.md) distinguishes implementation,
local functional evidence, and unvalidated deployment/capacity behavior.
