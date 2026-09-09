# Agentic AI Contact Center Accelerator

A public Azure solution accelerator for designing contact-center voice agents
with Azure Communication Services (ACS) Call Automation, Teams Phone
Extensibility (TPE), Dynamics 365 Contact Center integration patterns, and
pluggable realtime AI providers.

This repository captures architecture, decisions, provisioning automation,
operational guidance, and publication-safe lessons from a large contact-center
onboarding scenario. It is a reusable reference, not a claim that one topology
or capacity model fits every deployment.

> [!IMPORTANT]
> The [`infra/main.bicep`](infra/main.bicep) template provisions the modeled Zava
> Financial landing-zone infrastructure, but it has not been deployed or load
> tested as evidence. Treat scaling figures as modeled targets unless an evidence
> record explicitly says otherwise.

## What is here

| Area | Start here |
| --- | --- |
| End-to-end call path | [`docs/architecture/call-flow.md`](docs/architecture/call-flow.md) |
| Architecture views and reader map | [`docs/architecture/README.md`](docs/architecture/README.md) |
| Architecture decisions | [`docs/adr/README.md`](docs/adr/README.md) |
| TPE onboarding and automation | [`docs/runbooks/teams-configuration/tpe-onboarding-guide.md`](docs/runbooks/teams-configuration/tpe-onboarding-guide.md) |
| Transfer to a human agent | [`docs/architecture/transfer-patterns.md`](docs/architecture/transfer-patterns.md) |
| AKS topology and modeled scale tiers | [`docs/architecture/aks-topology.md`](docs/architecture/aks-topology.md) |
| Monitoring and correlation design | [`docs/monitoring/README.md`](docs/monitoring/README.md) |
| Operational guides | [`docs/runbooks/README.md`](docs/runbooks/README.md) |
| Evidence status and known gaps | [`docs/evidence/README.md`](docs/evidence/README.md) |

The machine-readable artifact map is
[`docs/solution-manifest.yaml`](docs/solution-manifest.yaml).

## Architecture at a glance

The reference flow keeps telephony control in ACS Call Automation and keeps the
application boundary on HTTPS callbacks and bidirectional media WebSockets:

1. A PSTN call reaches a Teams resource account through Teams Phone.
2. TPE connects that resource account to ACS.
3. Event Grid delivers the incoming-call event to the application.
4. ACS Call Automation owns call control and media streaming.
5. A conversation strategy drives realtime AI, NLU, or deterministic DTMF.
6. The call can escalate to a human agent through the selected contact-center
   transfer pattern.

See the [call-flow narrative](docs/architecture/call-flow.md) and
[sequence diagrams](docs/architecture/sequence-diagrams.md) for the detailed
interactions.

## Accelerator status

| Capability | Status |
| --- | --- |
| Architecture views and ADRs | Available; open decisions remain marked `Proposed` or `Draft` |
| TPE setup automation | Available for review and environment-specific testing |
| Monitoring model, KQL, and dashboard design | Reference artifacts; deployment assets are not fully migrated |
| Bicep infrastructure | Subscription-scope platform and N-region application-stamp template available; Azure deployment evidence is not yet recorded |
| .NET libraries and implementation tests | Present in [`code/ContactCenter.slnx`](code/ContactCenter.slnx); [445 local tests passed](docs/evidence/2026-09-09-dotnet-runtime-validation.md) for the runtime implementation patch |
| Aspire cloud/resource composition | Intentionally deferred pending SKU, region, and capacity recommendations; [`ContactCenter.AppHost`](code/ContactCenter.AppHost/AppHost.cs) remains empty |
| Runnable contact-center application | Host/endpoint composition is a separate adoption deliverable from Aspire resource provisioning |
| Python voice biometrics | [Optional gRPC prototype](code/voice-biometrics/); production inference and authentication assurance are not validated |
| Measured performance and failover evidence | Not yet recorded in this repository |

See the [current implementation map](docs/README.md#current-implementation)
before adopting the code. Present libraries and passing unit tests do not mean
that all documented flows, fallback tiers, or authentication policies are wired
and ready for customer use.

## Using the artifacts

Start with the architecture and ADRs before adapting scripts or topology. For
TPE onboarding, review the prerequisites and safeguards in the
[enterprise onboarding guide](docs/runbooks/teams-configuration/tpe-onboarding-guide.md),
then configure the samples under
[`scripts/teams-extensibility/`](scripts/teams-extensibility/) for a
non-production environment.

Do not treat sample limits, SKUs, retry values, or replica counts as universal
recommendations. Validate them against current Azure service documentation,
regional availability, quota, security policy, and workload evidence.

## Provisioning the Azure infrastructure

The AZD template deploys `zava-platform` plus one
`zava-contact-center-<region>` application landing-zone stamp and Microsoft
Foundry project in every configured region. Add regions by setting
`AZURE_ADDITIONAL_LOCATIONS` to a JSON array before provisioning:

```powershell
azd env set AZURE_LOCATION eastus2
azd env set AZURE_ADDITIONAL_LOCATIONS '["westus3"]'
azd provision
```

Each regional AI Services account has a private endpoint in its regional spoke.
The centralized private DNS zones cover Foundry, OpenAI, and the custom
`cognitiveservices.azure.com` endpoint used by speech-to-text, text-to-speech,
and Voice Live. `AZURE_AI_PROJECTS_JSON` contains the regional project and
endpoint inventory; the singular AI outputs remain aliases for the first
configured region. Set `AZURE_AI_PROJECT_BASE_NAME` to override the generated
regional project-name base.

Existing environments use incremental deployments, so provisioning the regional
projects does not delete a previous platform-hosted AI account. Remove that
account only after workloads have moved to the regional endpoints and the
regional projects have been validated.

The default AKS node sizes and counts follow the accepted architecture decisions
and can be expensive. Use the `AZURE_AKS_*` environment variables declared in
[`infra/main.parameters.json`](infra/main.parameters.json) to select
region-supported SKUs and demo-appropriate capacity. Provisioning requires
subscription-scope resource deployment and role-assignment permissions.

The platform resource group also contains one Azure Monitor workspace and one
Azure Managed Grafana workspace. Every regional AKS cluster enables the managed
Prometheus metrics add-on and receives its own data collection endpoint, rule,
and cluster association. AZD publishes the Grafana URL as
`AZURE_MANAGED_GRAFANA_ENDPOINT`.


## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) before changing architecture records,
evidence classifications, runbooks, or publication metadata. Accepted ADRs are
historical records; supersede them with a new ADR instead of rewriting their
decisions.

## License

Licensed under the terms in [`LICENSE`](LICENSE).
