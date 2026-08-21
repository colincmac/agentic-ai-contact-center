# Agents.AI.ContactCenter.Testing

Deterministic scenario testing for customer-authored contact-center workflows.

The package executes the real:

- YAML reader and workflow compiler;
- named transition predicates;
- `WorkflowExecutor`;
- inline credential authentication dispatcher;
- `StateFoldingChannelWriter`;
- IVR and authentication projections;
- workflow tool-name validation.

It does not construct `CallSession`, edges, media channels, Redis, Cosmos, or Azure resources.

Builders create one scenario session, and scenario actions are intentionally sequential. Create a new
builder/session for each independent or parallel test case.

## Basic workflow scenario

```csharp
await using var scenario = await ContactCenterScenario
    .FromYamlFile("Workflows/customer-service.yaml")
    .BuildAsync(TestContext.Current.CancellationToken);

var entered = await scenario.EnterAsync(TestContext.Current.CancellationToken);
entered
    .ShouldHaveReachedStage("welcome")
    .ShouldBeAtStage("welcome");

var outcome = await scenario.AdvanceAsync(
    "check-balance",
    TestContext.Current.CancellationToken);

Assert.IsType<AdvanceOutcome.Advanced>(outcome);
scenario.Snapshot()
    .ShouldHaveReachedStage("balance")
    .ShouldHaveWorkflowStatus(IvrWorkflowStatus.Completed);
```

`AdvanceAsync` accepts an authored transition label. It executes the exact edge and predicate selected by
that label. It does not classify natural-language utterances.

## Inline authentication

```csharp
await using var scenario = await ContactCenterScenario
    .ForWorkflow(workflow)
    .AddAuthenticator(new TestPinAuthenticator())
    .BuildAsync(TestContext.Current.CancellationToken);

var entered = await scenario.EnterAsync(TestContext.Current.CancellationToken);
entered.ShouldRequestCredential("Pin");

var result = await scenario.SubmitCredentialAsync(
    "Pin",
    "1234",
    TestContext.Current.CancellationToken);

result.ShouldHaveVerificationLevel(CallerVerificationLevel.KnowledgeBased);
```

## Tools and predicates

Register test tools and custom predicates before building:

```csharp
var scenario = ContactCenterScenario
    .ForWorkflow(workflow)
    .AddTool(testTool)
    .AddNamedPredicate("is-test-customer", context =>
        ValueTask.FromResult(PredicateResult.Pass()));
```

Unknown workflow tool names fail during `BuildAsync`, using the production compiler.

`ConfigureServices` is available when authenticators, tools, or predicates require supporting services.

## Assertions

Assertions are test-framework-neutral and throw `ScenarioAssertionException`:

- `ShouldHaveReachedStage`
- `ShouldBeAtStage`
- `ShouldHaveWorkflowStatus`
- `ShouldHaveVerificationLevel`
- `ShouldRequestCredential`
- `ShouldContainEvent<TEvent>`

`ScenarioResult` also exposes immutable snapshots of events, rendered stages, authentication prompts,
`IvrSnapshot`, and `AuthSnapshot` for normal xUnit, NUnit, or MSTest assertions.

## Deliberate first-slice limits

This package does not yet simulate:

- speech-to-text or natural-language intent classification;
- PCM audio, TTS, or media timing;
- full strategy fallback and provider outages;
- supervisor attach/whisper/barge-in;
- ACS callbacks or distributed failover.

Those require strategy and media-level harnesses. They will be added after semantic interaction outputs and
provider contracts stabilize, without changing the workflow scenario API.
