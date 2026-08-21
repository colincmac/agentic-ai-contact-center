# Migrating an existing repository onto this template

This guide is for repositories that already exist and want to adopt the
solution-artifact-template contract **without** disrupting how they
currently work. Prefer additive, incremental adoption over a big-bang
rewrite.

## Guiding principle: additive first

Add the contract on top of what you have; normalize later, if ever. Do not
treat this migration as a reason to reorganize a working repository.

## Step 1: add the manifest and indexes

1. Copy `docs/solution-manifest.yaml` and
   `docs/solution-manifest.schema.json` into your repository.
2. Populate `repository` (`owner`, `name`, `visibility`, and `defaultRef`) and
   point each `entryPoints` value at your **existing** documents, wherever
   they already live. You do not need to move anything yet.
   - If an area has no existing document, create a minimal index (you can
     copy `docs/*/README.md` from this template as a starting shell) and
     point the entry point at it.
3. Copy `docs/publishing/blog-brief.yaml` and its schema. Keep its review and
   candidates `not_proposed` until you intend to curate content for an
   external blog; the hand-off remains inert until then.
4. Copy `scripts/`, `test/`, `package.json`, and
   `.github/workflows/validate.yml` to get the validator and CI check.
5. Populate the publication brief's matching `solution` object and use its
   array of candidates. Each candidate carries provenance objects, ADR
   metadata: `current-record` requires `path` and `statusAsReviewed`;
   `reconstructed` requires a provenance `note` and prohibits those two
   fields. Evidence classes are `measured`, `modeled`, `assumed`, or `gap`.
6. Run `npm ci && npm test && npm run validate:initialized` and fix any reported
   issues (including missing entry points, broken fragments, or placeholders).
   A canonical-template checkout can validate the populated repository without
   modifying it using
   `npm run validate:conformance -- <repository-path>`.

## Step 2: preserve canonical paths

**Do not move existing ADRs, runbooks, or architecture documents merely to
match this template's example folder names.** A manifest entry point can
point anywhere in the repository; there is no requirement that ADRs live at
`docs/adr/`. Moving history-bearing documents breaks links (internal and
external), git blame continuity, and review context for no benefit.

If your existing layout differs from this template's, prefer:

- Keeping documents where they are.
- Updating `entryPoints` to point at your real paths.
- Adding a short index page next to your existing documents only if one
  does not already exist.

## Step 3: normalize metadata and validation over time

Once the manifest is in place and validating, you can optionally converge
further, at whatever pace fits your team:

- Adopt the ADR/runbook/evidence templates' sections for *new* documents,
  without rewriting old ones.
- Add missing sections (evidence classification, revisit triggers) to
  existing documents the next time they are naturally edited.
- Add a publication brief once you have real, reviewed content to propose
  to an external blog.
- Add per-path Copilot instructions (`.github/instructions/`) if you use
  GitHub Copilot in this repository.

Repositories already using a reconstructed decision with `path` or
`statusAsReviewed` must remove those fields and retain a non-empty provenance
`note`. A real contemporaneous ADR should instead be represented as
`kind: current-record`. This is a contract correction within schema version
`1.0.0`; no field aliases are supported.

## Keeping up with template changes

**Use this template** on GitHub is a one-time copy; it does not create an
ongoing link back to this template. To pull in a future improvement:

1. Compare this template's `VERSION` (and `docs/solution-manifest.yaml`
   `schemaVersion`) against your repository's.
2. Review this template's changelog/history for what changed between those
   versions.
3. Apply only the changes you want, by hand or via a diff/patch, following
   Step 1–3 above (additive first).
