---
applyTo: "docs/architecture/**/*.md"
description: Guidance for editing solution architecture documents.
---

# Architecture documentation instructions

- These are plain GitHub-flavored Markdown files, not MDX/Astro/Starlight.
  Do not introduce component imports or frontmatter beyond a simple
  title/metadata block.
- Every claim about reliability, security, or cost should link to the
  [ADR](../../docs/adr/README.md) that decided it or the
  [evidence record](../../docs/evidence/README.md) that measured it. Do not
  state a target as if it were a measured result.
- Keep the `## Deployment and provisioning` and `## Observability` section
  headings in `docs/README.md` stable, since
  `docs/solution-manifest.yaml` links to them by anchor; if you move that
  content elsewhere, update the manifest's `entryPoints` accordingly.
- When you add a new architecture view file, add a line for it under
  "Index" in `docs/architecture/README.md`.
- No credentials, tenant/subscription IDs, customer names, or private URLs.
