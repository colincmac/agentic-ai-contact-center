# Evidence record: REPLACE_ME (what was measured or validated)

> Copy this file, rename it (for example `2024-06-load-test.md`,
> `failover-drill.md`), and replace every REPLACE_ME. Plain GitHub-flavored
> Markdown, not MDX. Do not write this file until you actually have
> results, a model, or a stated assumption to record — do not imply
> evidence exists before it does.

- **Date:** REPLACE_ME (YYYY-MM-DD)
- **Related ADRs:** REPLACE_ME (see [`../adr/`](../../adr/README.md))
- **Class:** REPLACE_ME — one of `measured`, `modeled`, `assumed`, or `gap`
  (see [Classification](#classification))

## Classification

Pick exactly one, and be honest about it:

- **Measured** — an observed result from a real run (load test, production
  metric, failure drill actually executed).
- **Modeled** — a projected/estimated result from a model, calculation, or
  simulation, not directly observed.
- **Assumption** — a stated belief used as an input elsewhere, not tested
  here.
- **Gap** — a claim for which the needed measurement, model, or stated
  assumption has not yet been recorded.

REPLACE_ME: state which one this record is, and why.

## Method

REPLACE_ME: how the measurement/model was produced — tools, workload,
duration, sample size, and configuration. Enough detail that someone else
could reproduce it.

## Environment

REPLACE_ME: where this was run (environment tier, region class, scale),
using generic terms. No tenant IDs, subscription IDs, customer names, or
internal hostnames.

## Results

REPLACE_ME: the actual numbers or outcome, with units, and how they compare
to any target stated in the related architecture view or ADR. Do not round
away caveats.

## Assumptions

REPLACE_ME: assumptions made in the method, environment, or interpretation
of results that, if false, would invalidate or change this record.

## Limitations

REPLACE_ME: what this evidence does **not** show (for example, scale not
tested beyond X, no chaos/failure injection performed, single-region only).
This section is required even when it feels like an admission of weakness —
that is the point.

## Reproducibility

REPLACE_ME: whether and how this can be re-run, including any script,
workload definition, or dataset reference. If it cannot be reproduced,
state that and explain why (for example, "captured during a real incident,
not reproducible on demand").
