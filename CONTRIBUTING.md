# Contributing

Thanks for contributing to this solution repository. This is short by
design; see [`README.md`](README.md) for the full ownership model and
authoring flow.

## Before you open a pull request

- Run the validator: `npm ci && npm test`. It checks
  `docs/solution-manifest.yaml` and `docs/publishing/blog-brief.yaml`
  against their schemas, verifies local provenance and Markdown fragments,
  and confirms the manifest and brief agree.
- Keep documentation changes close to the code they describe.
- In a repository generated from this template, also run
  `npm run validate:initialized`; the canonical template itself intentionally
  retains `REPLACE_ME` placeholders.

## When do you need an ADR?

**Not for every code change.** Write an [ADR](docs/adr/README.md) when a
change is consequential and hard to reverse: it changes system shape,
introduces a new dependency, changes a reliability/security posture, or
reverses a prior decision. Routine implementation, refactors, and bug fixes
do not need one.

## When do you need to update architecture/runbooks/evidence?

- **Architecture:** if the change alters system shape, a quality attribute,
  or a stated constraint, update the relevant
  [architecture view](docs/architecture/README.md).
- **Runbooks:** if the change alters how an operational condition is
  diagnosed or mitigated, update the relevant
  [runbook](docs/runbooks/README.md).
- **Evidence:** if the change is justified by or produces new measured
  results, add or update an [evidence record](docs/evidence/README.md).
  Use only `measured`, `modeled`, `assumed`, or `gap` evidence classes; never
  let a PR imply a result that was not actually measured or modeled.

## Publication brief

Only touch [`docs/publishing/blog-brief.yaml`](docs/publishing/README.md)
when you have real, reviewed source material to propose. Leave placeholder
candidates as `not_proposed` otherwise; candidates are an array and may share
a format. Any change to this file needs a
human reviewer who understands the solution, in addition to normal code
review — see the PR template checklist.

In `canonicalAdrs`, use `kind: current-record` only for a contemporaneous ADR;
it requires `path` and `statusAsReviewed`. Use `kind: reconstructed` when no
such record exists; provide a provenance `note` and do not add `path` or
`statusAsReviewed`.

## Style

- Plain GitHub-flavored Markdown for all documentation (no MDX/Astro
  syntax).
- ASCII by default; keep comments concise and only where they add
  information the code/prose does not already convey.
- No credentials, tenant/subscription IDs, customer names, private URLs, or
  unsupported performance claims in any file.
