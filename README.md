# Solution Artifact Template

A reusable, framework-neutral GitHub repository template for solution
implementations. It keeps executable assets (code, infrastructure,
automation) together with the canonical architecture artifacts that explain
and govern them, while providing small, reviewed, publication-safe inputs to
an external technical blog.

This template does not scaffold an application, a cloud topology, or a
documentation site. It scaffolds the **contract** a solution repository uses
to stay coherent as it grows: a manifest of entry points, starter templates
for architecture/ADR/runbook/evidence content, and a publication brief that
an external blog can consume without ever depending on this repository at
build time.

## Who should use this template

Use this template when you are building or documenting a solution
implementation (or a technical investigation) that will:

- Own its own source of truth for architecture, decisions, and operations,
  regardless of language or stack (.NET, Java, Python, JavaScript,
  infrastructure-only, or mixed solutions).
- Need a lightweight, machine-checkable contract so tooling (including AI
  coding agents) can find the right documents without guessing.
- Optionally feed a curated, reviewed brief into an external blog or content
  pipeline, without giving that pipeline direct access to the repository.

Do **not** use this template as an application framework starter or an
infrastructure-as-code starter kit. It intentionally contains no
application source code and no cloud-specific deployment tooling.

## Ownership boundary: solution artifacts vs. blog synthesis

| Concern | Owned by | Where |
|---|---|---|
| Source code, infrastructure, automation | This repository | wherever your solution normally puts them |
| Architecture views, ADRs, runbooks, operational assets, evidence | This repository | `docs/architecture`, `docs/adr`, `docs/runbooks`, `docs/evidence` |
| Entry-point contract for tooling | This repository | `docs/solution-manifest.yaml` |
| Curated synthesis inputs for an external blog | This repository (authoring time only) | `docs/publishing/blog-brief.yaml` |
| Blog post drafting, editing, and publishing | The external blog, as a **separate repository/system** | not here |

This repository always owns **executable truth**. The external blog always
owns **synthesis**. The `docs/publishing/blog-brief.yaml` file is a curated,
human-reviewed hand-off: it is authored here, at your own pace, and is read
once by the blog's own authoring tooling to create a **separate draft** in
the blog's system. The production blog site build must never fetch from or
otherwise depend on this repository at build or deploy time.

Excluding something from the blog brief (or listing it under "excluded
material") is a **publication curation aid**, not a security boundary.
Repository permissions, secret scanning, sanitization, and human review
remain required regardless of what the brief says.

## Creating a repository from this template

1. Click **Use this template** on GitHub (or, if this repository is not yet
   marked as a template, ask the owner to enable **Settings > General >
   Template repository** first — that setting is not changed by this PR).
2. Clone your new repository.
3. Replace placeholders:
   - Update the title and description in this `README.md`.
   - Edit `docs/solution-manifest.yaml`: set the `repository` and `solution`
     objects, then update every entry point path to match your real documents
     (or remove optional entry points you do not use).
   - Rename/author your architecture, ADR, runbook, and evidence documents
     from the templates in `docs/architecture/templates/`,
     `docs/adr/templates/`, `docs/runbooks/templates/`, and
     `docs/evidence/templates/`.
   - Leave `docs/publishing/blog-brief.yaml` mostly empty until you have real,
     reviewed content to share with the blog; its placeholder values are
     intentionally obvious (for example `"REPLACE_ME"`) so you cannot
     accidentally publish them unedited.
4. Run `npm ci && npm test && npm run validate:initialized`. The final command
   deliberately fails while any `REPLACE_ME` value remains.

## Authoring flow

1. **Implement or investigate.** Build the solution, or run the technical
   investigation, in whatever stack fits. This template does not constrain
   that work.
2. **Record decisions.** Capture consequential decisions as ADRs in
   `docs/adr/` as you make them — not retroactively, and not for every
   trivial code change (see [`CONTRIBUTING.md`](CONTRIBUTING.md)).
3. **Maintain architecture, runbooks, and evidence.** Keep
   `docs/architecture/`, `docs/runbooks/`, and `docs/evidence/` current as
   the solution changes. Evidence records distinguish measured results from
   modeled targets, assumptions, and gaps; never imply proof you do not have.
4. **Curate a publication brief.** When (and only when) you want to propose
   external content, update `docs/publishing/blog-brief.yaml` with reviewed,
   sanitized pointers into the artifacts above.
5. **Let the blog create a separate draft.** The blog's own authoring
   tooling reads the brief at authoring time and creates a draft in its own
   system. This repository is never a runtime or build dependency of the
   published site.

## Template versioning and upgrades

GitHub's **Use this template** action is a **one-time copy**. It does not
create any ongoing link between this template and repositories created from
it, so future improvements to this template are **not** automatically
propagated to repositories that already exist.

- The current template contract version is recorded in the root
  [`VERSION`](VERSION) file and mirrored in
  `docs/solution-manifest.yaml` under `schemaVersion`.
- When this template changes in a way that affects the manifest or brief
  schema, the schema version is bumped and the change is documented.
- To adopt improvements in an existing repository created from this
  template, see [`docs/MIGRATION.md`](docs/MIGRATION.md): compare your
  `schemaVersion` to the current template version, and apply changes
  additively.

## Validation

This template ships a small, dependency-light Node.js validator (see
[`docs/README.md`](docs/README.md#validation)) that checks that
`docs/solution-manifest.yaml` and `docs/publishing/blog-brief.yaml`:

- parse as YAML,
- validate against their canonical JSON Schemas under `docs/`, and
- reference only existing, repository-relative, non-traversing local paths
  for entry points and publication provenance,
- resolve Markdown entry-point fragments, and
- keep the manifest and brief solution identity, repository, and visibility
  aligned.

```bash
npm ci
npm test
npm run validate:initialized
```

Template maintainers use `npm test`; the canonical starter intentionally
retains placeholders. Repositories created from the template additionally use
`npm run validate:initialized` before considering initialization complete.
From this canonical checkout, validate a populated external repository without
changing it by running:

```bash
npm run validate:conformance -- ../path-to-solution-repository
```

A publication candidate uses only the canonical fields `sourceArtifacts`,
`canonicalAdrs`, `detailsToGeneralize`, and `excludedMaterial`. Each
`canonicalAdrs` item is either a `current-record`, which requires `path` and
`statusAsReviewed`, or `reconstructed`, which requires a provenance `note` and
must not claim a contemporaneous path or status.

A GitHub Actions workflow (`.github/workflows/validate.yml`) runs the same
checks on every pull request and on pushes to the default branch.

## Repository layout

```
README.md                      This guide
VERSION                         Machine-readable template contract version
CONTRIBUTING.md                 Contributor guidance
docs/
  README.md                     Documentation hub
  solution-manifest.yaml        Entry-point contract for this solution
  solution-manifest.schema.json JSON Schema for the manifest
  MIGRATION.md                  Guide for adopting this template in an existing repo
  architecture/                 Architecture views + templates
  adr/                           Architecture Decision Records + template
  runbooks/                      Operational runbooks + template
  evidence/                      Evidence records + template
  publishing/                    Blog synthesis brief + schema + guidance
scripts/                        Validation tooling (Node.js)
test/                           Validator tests and fixtures
.github/
  workflows/validate.yml        CI validation
  PULL_REQUEST_TEMPLATE.md      PR checklist
  agents/                       Copilot custom agent definitions
  instructions/                 Copilot per-path instructions
```

## Owner action required after merge

Marking a repository as a **template repository** is a GitHub repository
setting (**Settings > General > Template repository**) and cannot be changed
by a pull request. After this content is merged, a repository owner must
enable that setting for **Use this template** to appear.

Repository rulesets, branch protection, automatic branch deletion, secret
scanning, and creation of the `v1.0.0` tag/release are owner-managed actions
after merge. This repository does not change those settings or create a
release automatically.
