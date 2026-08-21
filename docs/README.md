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

The .NET demonstrator referred to by some imported design records is not yet
present under `code/`. Those records describe the source showcase and the
intended accelerator design; they are not proof of implementation in this
repository.

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
reference designs; the complete dashboard panel assets and application
instrumentation have not yet been migrated.

## Operations

The current operational set covers Event Grid incoming-call subscription
behavior, timing/retry guidance, and Teams Phone Extensibility setup. Empty or
partial monitoring runbooks are tracked as gaps and are not considered
operational coverage. See [`runbooks/README.md`](runbooks/README.md).

## Evidence status

No measured evidence records are currently present. Performance, capacity, and
failover figures must be treated as modeled or assumed unless a future record
under [`evidence/`](evidence/) supplies method, environment, results, and
limitations.

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
