# ADR-0016: Host-neutral call contracts and method-bound caller verification

- **ID:** ADR-0016
- **Status:** proposed
- **Date:** 2026-09-09

## Context

The accelerator's code and imported workflow schema had diverged. Inline caller
verification could be satisfied by an unrelated method with the same numeric
rank, and retry exhaustion exposed the protected business stage. Stateful speech
and application hosting also needed boundaries separate from resource provisioning.

## Decision drivers

- Keep new accelerator components in the existing .NET project for now.
- Keep Azure SKU, region, capacity provisioning, and Aspire cloud wiring deferred.
- Support DTMF, NLU, and realtime through one business-flow/authentication contract.
- Reuse compatible Agent Framework APIs without placing live media in durable state.
- Fail closed with explicit configured authentication failure routes.

## Considered alternatives

1. **Adopt Orleans now.** Deferred by the implementation owner; establish local
   conformance first. Media ownership would remain a separate concern.
2. **Adopt native MAF declarative YAML immediately.** Not selected without a proven
   IVR action extension path. The inspected public builder supports agent/HTTP/MCP
   integration, not a verified drop-in replacement for this call runtime.
3. **Keep the legacy dialect and assurance-rank substitution.** Rejected: silent
   field loss and unrelated-method substitution undermine predictable call flows.
4. **A host-neutral call runtime with a discrete MAF executor adapter.** Implemented
   for review using the existing pinned workflow package.

## Decision

Propose four distinct contracts: trusted ingress routing, revisioned call flow,
named interaction profiles, and required caller evidence. Keep new components
under `Agents.AI.ContactCenter`; add no projects or Orleans dependency in this pass.

Use strict v1 YAML and explicit `id@version` selection. Required authentication
must specify a valid unprotected failure stage. Reusable proof must match method,
subject, and freshness; a numeric rank alone cannot satisfy a named method.
Numeric credential capture is deterministic and isolated from model input and
conversation events. Business actions use resource-based authorization and a
stable backend idempotency key.

Use Agent Framework's existing executor API for discrete call commands. A live
client session is not serialized with a workflow. Recorded DTMF requires a
verb-capable edge and callback-correlated playback/collection.

This proposal does not change accepted topology, ingress, or physical
media-owner decisions, nor claim a complete distributed workflow engine.

## Consequences

- Old YAML samples/configurations must be explicitly migrated; unknown fields fail.
- Old proof snapshots lacking subject/time metadata require re-verification.
- Distributed challenge stores must implement atomic call-bound consumption.
- Backend action implementations remain responsible for external idempotency;
  a call-local cache is not an exactly-once transaction guarantee.
- A media failure cannot be solved by changing only the strategy on an incompatible
  edge. Host-controlled edge replacement or overflow remains necessary.

### Operational implications

Follow the existing [operations index](../runbooks/README.md). Configure capacities,
recorded assets, explicit failure routes, and host/provider registrations before
starting the application. Deployment recommendations remain pending; no infrastructure
or PowerShell changes are part of this decision.

### Security implications

Transport, caller, and operator identities remain separate. Verification failure
must not expose protected actions. Model output is not proof. Required observer or
persistence failure cannot silently discard authoritative call state.

## Evidence

The [initial local baseline](../evidence/2026-09-08-code-adoption-validation.md)
included the old fail-open assertion and is not evidence of safe verification.
New [workflow tests](../../code/test/Agents.AI.ContactCenter.Tests/IvrWorkflow/),
[authentication tests](../../code/test/Agents.AI.ContactCenter.Tests/Authentication/),
and [state tests](../../code/test/Agents.AI.ContactCenter.Tests/State/) exercise the
candidate implementation; [the local run is recorded](../evidence/2026-09-09-dotnet-runtime-validation.md).
No deployment, latency, hyperscale, or model-quality
evidence informed the capacity aspects of this proposal.

## Revisit triggers

- A stable declarative IVR/custom-action extension path can satisfy the same tests.
- Distributed coordinator requirements justify Orleans or Durable Task.
- Recorded credential capture, speech-based verification, or new transports are needed.
- Measured load/failover evidence requires a different persistence or admission design.
