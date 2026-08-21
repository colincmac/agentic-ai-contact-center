---
applyTo: "docs/runbooks/**/*.md"
description: Guidance for authoring and maintaining operational runbooks.
---

# Runbook instructions

- Copy [`docs/runbooks/templates/runbook.template.md`](../../docs/runbooks/templates/runbook.template.md)
  for new runbooks; keep all sections, including Safeguards and
  Escalation, even when short.
- Write triggers specifically enough that the right runbook can be found
  from an alert name or dashboard panel, not just a vague symptom.
- Keep mitigation steps ordered and testable; state the expected result
  after each step.
- Link the ADR(s) that explain why the mitigation is the accepted approach,
  and the evidence record template for capturing outcomes after use.
- Add a line to the index in `docs/runbooks/README.md` for every new
  runbook.
- Update the runbook promptly when the system it describes changes; a
  stale runbook is worse than none because it is trusted.
