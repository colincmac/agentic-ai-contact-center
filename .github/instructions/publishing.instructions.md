---
applyTo: "docs/publishing/**"
description: Guidance for curating the publication brief for the external blog.
---

# Publishing instructions

- Only edit [`docs/publishing/blog-brief.yaml`](../../docs/publishing/blog-brief.yaml)
  using content that already exists elsewhere in this repository
  (architecture, ADRs, runbooks, evidence). Never fabricate a takeaway,
  audience, or piece of evidence.
- Keep unresolved/placeholder candidates set to `reviewStatus: not_proposed`
  rather than guessing at content.
- Record `evidenceGaps` and `excludedMaterial` explicitly instead of
  silently omitting them.
- Use only the canonical candidate fields `sourceArtifacts`, `canonicalAdrs`,
  `detailsToGeneralize`, and `excludedMaterial`; do not introduce aliases.
- In `canonicalAdrs`, a `current-record` requires a real `path` and
  `statusAsReviewed`; a `reconstructed` decision requires a non-empty
  provenance `note` and must not include either field. Never present a
  retrospective reconstruction as a contemporaneous ADR.
- Run `npm test` after editing to confirm the brief still validates against
  `docs/publishing/blog-brief.schema.json` and that referenced paths exist.
- **Never publish.** This repository only produces the brief; the external
  blog's own authoring tooling reads it, at authoring time, to create a
  separate draft. Do not add code or automation here that calls out to a
  blog system.
- Treat `globalExclusions` and `synthesisExclusions` as curation aids for
  human reviewers, not as security controls — sanitization and review are
  still required.
