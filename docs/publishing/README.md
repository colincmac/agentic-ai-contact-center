# Publishing

This folder is where this repository curates a **publication brief** for an
external technical blog. It is the only part of this repository that the
blog's own authoring tooling is expected to read, and only at authoring
time — never at build or deploy time.

## Ownership boundary

- This repository owns the brief: a small, reviewed set of pointers into
  real [architecture](../architecture/README.md), [ADR](../adr/README.md),
  [runbook](../runbooks/README.md), and [evidence](../evidence/README.md)
  documents, plus notes on what to generalize and what to exclude.
- The external blog owns synthesis: turning the brief into an actual draft,
  choosing narrative and framing, and publishing.
- The blog's production site build must never depend on this repository.
  The brief is read once, by authoring tooling, to create a separate draft
  elsewhere.

Excluding something here (or in `synthesisExclusions` in
[`../solution-manifest.yaml`](../solution-manifest.yaml)) is a curation aid,
**not** a security boundary. Repository permissions, sanitization, and human
review remain required regardless of what this brief says.

## Supported formats

The brief uses a `candidates` array, so it can propose multiple posts in any
of four supported formats:

- **Architectural decision** — a single consequential decision, its
  alternatives, and its consequences.
- **Large-scale lessons** — lessons learned operating this solution at
  scale, backed by evidence.
- **Reference architecture** — a reusable pattern illustrated by this
  solution's architecture.
- **Field note** — a short, narrow observation from building or operating
  this solution.

## Starter content

- [`blog-brief.yaml`](blog-brief.yaml) — the brief itself. Placeholder
  values are intentionally obvious (for example `"REPLACE_ME"`) so it
  cannot be mistaken for reviewed content.
- [`../publishing/blog-brief.schema.json`](blog-brief.schema.json) — JSON
  Schema used by `npm test` to catch malformed briefs before they are ever
  read by anything external.

Each candidate has a stable `id`, `format`, working title, audience, takeaway,
local source/operations provenance, ADR objects, and evidence objects. ADRs
use one of two shapes:

- A `current-record` requires `id`, `kind`, `path`, and
  `statusAsReviewed` (`proposed`, `accepted`, `superseded`, or `deprecated`).
- A `reconstructed` decision requires `id`, `kind`, and a non-empty provenance
  `note`. It must not include `path` or `statusAsReviewed`, because no
  contemporaneous ADR exists.

Evidence uses only `measured`, `modeled`, `assumed`, and `gap`. Canonical
candidate field names are `sourceArtifacts`, `canonicalAdrs`,
`detailsToGeneralize`, and `excludedMaterial`; aliases are not accepted.

## Review checklist before marking a candidate ready

- [ ] Every source artifact referenced actually exists and is current.
- [ ] Every claim maps to an ADR, runbook, or evidence record — not memory.
- [ ] Reconstructed decisions include provenance and do not imply that a
      contemporaneous ADR exists.
- [ ] Evidence gaps are stated explicitly, not implied to be resolved.
- [ ] No credentials, tenant/subscription IDs, customer names, private
      URLs, or unsupported performance claims are present.
- [ ] A human who understands this solution has reviewed the entry.
