# Documentation hub

This is the human-facing entry point into this solution's documentation. It
mirrors the machine-readable contract in
[`solution-manifest.yaml`](solution-manifest.yaml): every area listed there
has a corresponding section and index below.

> Replace this page's content with a real overview of your solution once you
> create a repository from this template. Keep the section headings stable
> (or update the matching anchors in `solution-manifest.yaml`) so tooling
> that reads the manifest keeps working.

## Solution overview

REPLACE_ME: describe what this solution is, who it is for, and what it is
not. Link out to the areas below for detail; keep this page short.

## Architecture

See [`docs/architecture/README.md`](architecture/README.md) for architecture
views, system shape, and non-functional characteristics.

### Deployment and provisioning

REPLACE_ME: describe how this solution is deployed or provisioned, or state
explicitly that it is not deployable (for example, a library or an
investigation-only repository). This section is the target of the
`entryPoints.deployment` anchor in the manifest; keep the heading id
(`deployment-and-provisioning`) stable, or update the manifest if you move
this content into its own document.

### Observability

REPLACE_ME: describe or link to dashboards, alerts, logs, and traces for
this solution. This section is the target of the `entryPoints.observability`
anchor in the manifest.

## Decisions

See [`docs/adr/README.md`](adr/README.md) for the Architecture Decision
Record index.

## Operations

See [`docs/runbooks/README.md`](runbooks/README.md) for operational
runbooks.

## Evidence

See [`docs/evidence/README.md`](evidence/README.md) for measured results,
validated drills, modeled results, assumptions, gaps, and limitations.

## Publishing

See [`docs/publishing/README.md`](publishing/README.md) for how this
solution curates a publication brief for an external blog, and the
ownership boundary that keeps synthesis outside this repository.

## Validation

This template ships a Node.js validator that checks
`docs/solution-manifest.yaml` and `docs/publishing/blog-brief.yaml` against their JSON Schemas, confirms local
provenance paths and Markdown fragments exist, and checks that their solution
ID, repository owner/name, and source visibility agree.

```bash
npm ci
npm test
npm run validate:initialized
```

`npm test` validates the canonical template while retaining its placeholders.
`npm run validate:initialized` is for repositories created from this template;
it fails until every `REPLACE_ME` value in the manifest and brief is replaced.
Canonical-template maintainers can read-only validate another populated
repository against these schemas with
`npm run validate:conformance -- <repository-path>`.

See the repository root [`README.md`](../README.md) for the full authoring
flow and template versioning notes.

## Migrating an existing repository onto this template

See [`docs/MIGRATION.md`](MIGRATION.md).
