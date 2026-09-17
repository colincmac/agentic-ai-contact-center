---
applyTo: "docs/**/*.md"
description: Write customer-facing, adoption-focused documentation for an Azure solution accelerator.
---

# Documentation audience and style

## Audience

- Write for architects, engineers, and operators adopting this accelerator
  or applying its patterns to their own solution.
- Treat the accelerator as reference guidance, not a production application.
- Explain the design independently of the demo application's implementation.

## Structure and readability

- Start with the reader's problem and a concise recommended starting point.
- Define unfamiliar terms and acronyms before using them.
- Explain options through their benefits, costs, limitations, and operational
  trade-offs. Use compact comparison tables where they help.
- Distinguish recommendations, supported alternatives, and decisions the
  adopter must make.
- Use plain language, short paragraphs, descriptive headings, and concrete
  examples. Avoid internal project terminology and repeated caveats.
- Put detailed recovery steps in runbooks rather than repeating them in
  conceptual guides and ADRs.

## Implementation independence

- Do not include repository inventories, infrastructure-as-code details,
  .NET/demo code references, or implementation-status commentary in
  conceptual documentation unless explicitly requested.
- Include implementation details in setup, deployment, or code documentation
  when they are necessary for the reader's task.
- Verify accuracy against relevant sources without turning the document
  into a report of the agent's investigation.

## Accuracy and evidence

- Preserve material limitations, prerequisites, safety guidance, and
  distinctions between automatic behavior and customer-managed actions.
- Cite official documentation for service capabilities and restrictions.
- Clearly label illustrative estimates and untested recovery assumptions.
- Use one concise scope/status note where appropriate; do not repeat
  production-readiness disclaimers throughout the document.
- Never imply that a reference design is deployed or validated.

## Document types

- Guides explain what to choose, when to choose it, and the trade-offs.
- ADRs explain the context, alternatives, proposed or accepted decision,
  and consequences. Preserve required sections and accepted decision history.
- Runbooks explain triggers, prerequisites, safe actions, expected results,
  verification, rollback, and escalation.
- Preserve the specialized requirements for evidence records and publishing.

Use docs/monitoring/high-availability.md and
docs/adr/0017-telemetry-high-availability-and-disaster-recovery.md
as examples of the preferred audience and presentation, not as text to copy.