# Architecture

This folder is the canonical source of truth for this solution's
architecture: system shape, quality attributes, and the operational
characteristics that follow from them. Nothing here should imply outcomes
that are not backed by an [ADR](../adr/README.md) or an
[evidence record](../evidence/README.md).

## Ownership

Architecture documents are owned and maintained by this repository. They
are the input that a curated [publication brief](../publishing/README.md)
may later summarize for an external blog — the brief never becomes the
canonical copy, this folder is.

## What goes here

- One or more architecture views (system overview, and additional views for
  specific concerns such as data flow, security, or deployment topology).
- Links out to related ADRs, runbooks, and evidence for anything you assert
  (a constraint, a failure mode, a reliability target).

## Starter content

- [`templates/architecture-view.template.md`](templates/architecture-view.template.md) —
  copy this to create a new architecture view (for example
  `overview.md`, `data-flow.md`, or `security-view.md`).

## Index

REPLACE_ME: list your architecture views here as you create them, for
example:

- `overview.md` — system overview (start here)

Until real views exist, use the template above as the starting point and
link the resulting document(s) here.
