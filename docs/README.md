# Solution documentation

Canonical architecture, decision, operations, evidence, and publication
artifacts for the Agentic AI Contact Center Accelerator.

## Start here

| Entry point | Purpose |
| --- | --- |
| [`solution-manifest.yaml`](solution-manifest.yaml) | Machine-readable repository identity and canonical artifact paths. |
| [`architecture/README.md`](architecture/README.md) | Reader-oriented map of call flow, conversation, deployment, and monitoring views. |
| [`adr/README.md`](adr/README.md) | Complete decision index with authoritative status links. |
| [`runbooks/README.md`](runbooks/README.md) | Operational and TPE onboarding guides. |
| [`evidence/README.md`](evidence/README.md) | Evidence classifications, current gaps, and future record index. |
| [`publishing/blog-brief.yaml`](publishing/blog-brief.yaml) | Reviewed synthesis metadata for an external blog draft. |

## Architecture

| Document | Scope |
| --- | --- |
| [`architecture/call-flow.md`](architecture/call-flow.md) | PSTN to Teams resource account, ACS Call Automation, IVR, and contact-center transfer. |
| [`architecture/sequence-diagrams.md`](architecture/sequence-diagrams.md) | End-to-end interaction sequences. |
| [`architecture/transfer-patterns.md`](architecture/transfer-patterns.md) | Blind and consultative transfer patterns. |
| [`architecture/aks-topology.md`](architecture/aks-topology.md) | Modeled AKS topology, routing, ownership, and scale tiers. |
| [`strategies/README.md`](strategies/README.md) | Conversation strategy catalog and degradation model. |
| [`strategies/conversation-strategies.md`](strategies/conversation-strategies.md) | Detailed strategy contract and events. |

## Current implementation

The [.NET solution](../code/ContactCenter.slnx) and
[Python biometric service](../code/voice-biometrics/) are present. The solution
is still a prototype, not an end-to-end runnable contact-center application.
Imported design records describe the intended system and may name APIs from
the earlier showcase. Accepted ADRs remain the design authority; implementation
differences below are not revisions to those dec

Aspire cloud/resource wiring is intentionally deferred while the recommended
SKUs, regions, and capacity configuration are confirmed. The empty AppHost is
expected at this stage, not a library defect. Application endpoint composition,
flow conformance, and caller-authentication tests can be reviewed independently
of those deployment choices; this code review does not populate the AppHost.

| Area | Current code | Adoption boundary |
| --- | --- | --- |
| Hosting and ingress | [Call-control library](../code/Agents.AI.ContactCenter/Calling/) and [AppHost](../code/ContactCenter.AppHost/AppHost.cs) | Aspire resource composition is deliberately deferred. The Event Grid answer handler, callback endpoints, and authenticated media endpoint remain a separate application-integration deliverable; see [ADR-0001](adr/0001-pstn-ingress-via-tpe.md), [ADR-0002](adr/0002-acs-call-automation-as-control-plane.md), and [ADR-0003](adr/0003-incomingcall-delivery-via-event-grid.md). |
| Conversation strategies | [Streaming registrations](../code/Agents.AI.ContactCenter/DependencyInjection/CallWorkflowStrategyExtensions.cs) and [recorded DTMF](../code/Agents.AI.ContactCenter/Calling/Strategies/Dtmf/RecordedDtmfCallWorkflowStrategy.cs) | The recorded adapter uses ACS verbs/callbacks without live Speech. It needs recorded assets and a compatible edge; automatic streaming-to-verb edge replacement is still host integration. Chat-completion/TTS and SLM are not supplied as additional leaf strategies. |
| Flow authoring | [Current YAML reader](../code/Agents.AI.ContactCenter/IvrWorkflow/Loading/CallWorkflowYamlReader.cs), [compiler](../code/Agents.AI.ContactCenter/IvrWorkflow/Compilation/WorkflowGraphCompiler.cs), and [executor](../code/Agents.AI.ContactCenter/IvrWorkflow/Execution/WorkflowExecutor.cs) | Strict v1 schema/samples use `id` and `initialStage`; explicit revisions coexist as `id@version`. A [discrete Agent Framework adapter](../code/Agents.AI.ContactCenter/IvrWorkflow/AgentFramework/CallWorkflowCommandExecutor.cs) uses the pinned SDK. No visual editor, VXML importer, or native MAF declarative dialect is supplied. |
| Caller identity | [Authentication providers](../code/Agents.AI.ContactCenter/Authentication/) and [event-folded auth state](../code/Agents.AI.ContactCenter/State/Projections/AuthStateProjection.cs) | Required methods use subject/freshness evidence and explicit failure routes; numeric credentials use trusted DTMF capture. The old fail-open assertion in the historical baseline has been replaced. Production provider assurance and other capture modalities still require validation; see [ADR-0016](adr/0016-host-neutral-call-contracts-and-verification.md). |
| Speech isolation | [Speech adapters](../code/Agents.AI.ContactCenter/Azure/README.md) | Recognizers and NLU agents are call-scoped; synthesis remains shared without synchronous network warm-up. Local scope/concurrency tests do not establish live service capacity. |
| State and coordination | [State projections/stores](../code/Agents.AI.ContactCenter/State/) and [coordination primitives](../code/Agents.AI.ContactCenter/Coordination/) | These implement parts of [ADR-0004](adr/0004-call-state-in-redis-by-callconnectionid.md), [ADR-0011](adr/0011-pod-ownership-and-lease-model.md), and [ADR-0014](adr/0014-call-state-event-folded-provider-slices.md); distributed runtime behavior is not established by local unit tests. |
| Biometrics | [Python service](../code/voice-biometrics/) and [.NET adapter](../code/Agents.AI.ContactCenter/Authorization/Biometrics/ApiBiometricEvaluator.cs) | Optional prototype behind the [ADR-0009](adr/0009-voice-biometrics-stub-vs-grpc.md) seam; no calibrated biometric assurance or production inference evidence is recorded. |
| Monitoring | [Monitoring library](../code/Agents.AI.Monitoring/) and [design index](monitoring/README.md) | Library and reference assets are present; host wiring and real ACS/Teams/D365 correlation still need validation. |

The [current implementation validation](evidence/2026-09-09-dotnet-runtime-validation.md)
records 445 passing local tests across the affected projects, not readiness or
hyperscale certification. The [earlier baseline](evidence/2026-09-08-code-adoption-validation.md)
is retained as historical evidence.
Current contracts and migration notes are in the
[call-workflow guide](../code/Agents.AI.ContactCenter/IvrWorkflow/README.md).
Use the [strategy implementation map](strategies/README.md) rather than copying
the historical strategy snippets as current API examples.

## Deployment and provisioning

| Artifact | Scope |
| --- | --- |
| [`runbooks/teams-configuration/teams-extensibility.md`](runbooks/teams-configuration/teams-extensibility.md) | TPE overview and official platform entry points. |
| [`runbooks/teams-configuration/tpe-onboarding-guide.md`](runbooks/teams-configuration/tpe-onboarding-guide.md) | Greenfield TPE onboarding. |
| [`runbooks/teams-configuration/tpe-brownfield.md`](runbooks/teams-configuration/tpe-brownfield.md) | Existing Teams/ACS resource onboarding. |
| [`../scripts/teams-extensibility/`](../scripts/teams-extensibility/) | PowerShell provisioning and cleanup automation. |
| [`architecture/aks-topology.md`](architecture/aks-topology.md) | Deployment topology and modeled sizing method. |

[`../infra/main.bicep`](../infra/main.bicep) is currently a placeholder and
must not be represented as a deployable infrastructure template.

## Observability

The [`monitoring/README.md`](monitoring/README.md) index links the correlation
model, KQL reference, dashboards, costs, and setup guidance. These documents are
reference designs alongside the [monitoring library](../code/Agents.AI.Monitoring/).
The empty AppHost does not compose a monitored contact-center application, and
cross-platform correlation has not been validated against live traffic.

## Operations

The current operational set covers Event Grid incoming-call subscription
behavior, timing/retry guidance, and Teams Phone Extensibility setup. Empty or
partial monitoring runbooks are tracked as gaps and are not considered
operational coverage. See [`runbooks/README.md`](runbooks/README.md).

## Evidence status

[Measured local functional test records](evidence/README.md)
are present. They are not performance, capacity, or live failover evidence. Those figures
remain modeled or assumed until a record under [`evidence/`](evidence/) supplies
the relevant method, environment, results, and limitations.

## Publication

[`publishing/blog-brief.yaml`](publishing/blog-brief.yaml) proposes four article
shapes based only on artifacts in this repository. It is not blog prose and it
does not publish anything. Review the rules in
[`publishing/README.md`](publishing/README.md) before changing it.

## Validation

```powershell
npm ci
npm test
npm run validate:initialized
```

The validator checks the manifest and brief schemas, declared local paths and
fragments, cross-document identity, exclusions, and customization frontmatter.
It does not validate every Markdown link or prove deployment behavior.
