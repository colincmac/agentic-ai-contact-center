# Evidence record: focused code-adoption validation

- **Date:** 2026-09-08
- **Related ADRs:** [ADR-0008](../adr/0008-graceful-degradation-realtime-to-dtmf.md),
  [ADR-0014](../adr/0014-call-state-event-folded-provider-slices.md)
- **Class:** measured

## Classification

An observed local unit-test run against the existing .NET implementation.
This record measures test outcomes, not contact-center capacity, caller
authentication assurance, or deployment readiness.

## Method

Build and run a targeted selection from
[`Agents.AI.ContactCenter.Tests`](../../code/test/Agents.AI.ContactCenter.Tests/Agents.AI.ContactCenter.Tests.csproj)
using the existing VSTest/xUnit v3 test runner. Select the YAML reader/compiler,
inline-authentication driver, authentication tests, composite fallback,
distributed tier resolver, standard registration facade, and ACS verb edge.

No source changes were made to these .NET projects before the run. Existing
restored dependencies were used; no package update or installation was part
of this validation.

## Environment

- Local Windows development workstation; no Azure deployment.
- .NET SDK 10.0.400, target framework `net10.0`.
- Repository baseline: commit `3af073ea447c08e8c55511623203fc39201f76da`.
- Tests use their existing local fakes/helpers; no live calls or model inference.

## Results

| Measure | Result |
| --- | ---: |
| Tests executed | 67 |
| Passed | 67 |
| Failed | 0 |
| Skipped | 0 |

The target test project and its referenced .NET projects built successfully.
No comparison with a throughput, latency, or concurrent-call target was made.

## Assumptions

- The existing test assertions define what this test run considers success;
  they are not independently approved business or authentication requirements.
- The selected tests and repository dependency versions are preserved when
  comparing a later run with these results.

## Limitations

- This is a selected test run, not the full solution suite.
- Passing tests do not establish safe caller-authentication behavior.
  [`Plan_ExhaustsRetries_FallsThroughToBusiness`](../../code/test/Agents.AI.ContactCenter.Tests/IvrWorkflow/Execution/InlineAuthDriverTests.cs)
  explicitly expects an unverified caller to enter the business stage after
  retry exhaustion. That assertion passed; it is not a production recommendation.
- The run does not exercise concurrent calls through the real speech-service
  DI registration, every shipped YAML sample, or a composed ACS/TPE host.
- No Redis/Cosmos deployment, live callback/media authentication, human transfer,
  provider quota exhaustion, pod loss, region drain, soak, or load test was run.
- No Python biometric inference or clean-baseline Python service run is included.
- These results do not certify the modeled tier capacities or failover claims
  in the related ADRs.

## Reproducibility

From the repository's `code` directory, with the declared .NET SDK/dependencies
available, the recorded invocation was:

```powershell
dotnet test '.\test\Agents.AI.ContactCenter.Tests\Agents.AI.ContactCenter.Tests.csproj' --no-restore --filter 'FullyQualifiedName~CallWorkflowYamlReaderTests|FullyQualifiedName~WorkflowGraphCompilerTests|FullyQualifiedName~InlineAuthDriverTests|FullyQualifiedName~AuthenticationTests|FullyQualifiedName~CompositeFallbackStrategyTests|FullyQualifiedName~DistributedAgentTierResolverTests|FullyQualifiedName~StandardContactCenterExtensionsTests|FullyQualifiedName~AcsCallAutomationEdgeTests' --verbosity minimal
```

`--no-restore` assumes the declared dependencies have already been restored.
Record the source revision and selected test count when repeating the run;
future refactors can legitimately change the test selection and results.
