---
applyTo: "docs/adr/**/*.md"
description: Guidance for authoring and maintaining Architecture Decision Records.
---

# ADR instructions

- Copy [`docs/adr/templates/adr.template.md`](../../docs/adr/templates/adr.template.md)
  for new ADRs; do not delete required sections even if brief.
- Use the next sequential, zero-padded four-digit ID
  (`0001-`, `0002-`, ...). IDs are stable once assigned and are never
  reused.
- **Never rewrite an `accepted` ADR's decision after the fact.** If a
  decision changes, write a new ADR and mark the old one
  `superseded by ADR-NNNN`.
- List at least one credible alternative that was actually considered; if
  there truly was only one viable option, say so and explain why.
- Link evidence that informed the decision; if none exists, say so rather
  than implying validation that did not happen.
- Add a row to the index table in `docs/adr/README.md` for every new ADR.
