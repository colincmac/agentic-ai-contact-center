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
differences below are not revisions to those decisions.

| Area | Current code | Adoption boundary |
| --- | --- | --- |
| Hosting and ingress | [Call-control library](../code/Agents.AI.ContactCenter/Calling/) and [AppHost](../code/ContactCenter.AppHost/AppHost.cs) | AppHost registers no application services. The Event Grid answer handler, callback endpoints, and authenticated media endpoint still need host composition; see [ADR-0001](adr/0001-pstn-ingress-via-tpe.md), [ADR-0002](adr/0002-acs-call-automation-as-control-plane.md), and [ADR-0003](adr/0003-incomingcall-delivery-via-event-grid.md). |
| Conversation strategies | [Realtime, NLU, DTMF, and composite registrations](../code/Agents.AI.ContactCenter/DependencyInjection/CallWorkflowStrategyExtensions.cs) | Chat-completion/TTS and SLM tiers are configuration entries, not built-in strategies. Current DTMF renders streamed TTS; it does not implement the independent prerecorded fallback in [ADR-0008](adr/0008-graceful-degradation-realtime-to-dtmf.md). |
| Flow authoring | [Current YAML reader](../code/Agents.AI.ContactCenter/IvrWorkflow/Loading/CallWorkflowYamlReader.cs), [compiler](../code/Agents.AI.ContactCenter/IvrWorkflow/Compilation/WorkflowGraphCompiler.cs), and [executor](../code/Agents.AI.ContactCenter/IvrWorkflow/Execution/WorkflowExecutor.cs) | The reader requires `id` and `initialStage`. The shipped schema, older samples, and library README still describe another dialect. The catalog is keyed by ID alone; no business-user editor or VXML importer is supplied. |
| Caller identity | [Authentication providers](../code/Agents.AI.ContactCenter/Authentication/) and [event-folded auth state](../code/Agents.AI.ContactCenter/State/Projections/AuthStateProjection.cs) | Named-method enforcement, failure handling, secret capture, and cross-call speech isolation require hardening. In particular, the current inline-auth test expects exhausted retries to enter the business stage unverified; see the [validation limitations](evidence/2026-09-08-code-adoption-validation.md#limitations). |
| State and coordination | [State projections/stores](../code/Agents.AI.ContactCenter/State/) and [coordination primitives](../code/Agents.AI.ContactCenter/Coordination/) | These implement parts of [ADR-0004](adr/0004-call-state-in-redis-by-callconnectionid.md), [ADR-0011](adr/0011-pod-ownership-and-lease-model.md), and [ADR-0014](adr/0014-call-state-event-folded-provider-slices.md); distributed runtime behavior is not established by local unit tests. |
| Biometrics | [Python service](../code/voice-biometrics/) and [.NET adapter](../code/Agents.AI.ContactCenter/Authorization/Biometrics/ApiBiometricEvaluator.cs) | Optional prototype behind the [ADR-0009](adr/0009-voice-biometrics-stub-vs-grpc.md) seam; no calibrated biometric assurance or production inference evidence is recorded. |
| Monitoring | [Monitoring library](../code/Agents.AI.Monitoring/) and [design index](monitoring/README.md) | Library and reference assets are present; host wiring and real ACS/Teams/D365 correlation still need validation. |

The [functional validation record](evidence/2026-09-08-code-adoption-validation.md)
captures a focused local test run, not a readiness or hyperscale certification.
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

A [measured local functional test record](evidence/2026-09-08-code-adoption-validation.md)
is present. It is not performance, capacity, or failover evidence. Those figures
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
