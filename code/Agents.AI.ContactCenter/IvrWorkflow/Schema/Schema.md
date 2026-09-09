# Call workflow v1 field reference

Use [ivr-workflow.schema.json](ivr-workflow.schema.json) with the
[runtime reader](../Loading/CallWorkflowYamlReader.cs). YAML unknown properties and
duplicate keys are errors. Reference validation also runs during compilation and
startup binding; JSON Schema alone cannot verify registrations.

## Root

Required: `id`, `initialStage`, and a nonempty `stages` array.
Optional: `schemaVersion` (1), `version` (positive integer, default 1),
`description`, `basePrompt`, and `commonTools` (registered function names).
An ID cannot contain `@`; use `id@version` when selecting a revision.

## Stages

| Field | Meaning |
| --- | --- |
| `id` | Required unique stage ID |
| `goal`, `description`, `exitWhen` | Presentation and intent hints, not executable action conditions |
| `terminal`, `terminalOutcome` | Logical completion and `none`, `success`, `failure`, `escalated`, or `abandoned` outcome |
| `tools` | Additional registered realtime function names |
| `authenticate` | Ordered groups with either `use: Method` or `anyOf: [MethodA, MethodB]` |
| `onAuthenticationFailure` | Mandatory existing unprotected stage when `authenticate` is present |
| `maxAuthenticationAttempts` | Positive retry limit, default 3 |
| `authenticationMaxAgeSeconds` | Positive evidence freshness limit, default 300 |
| `interactionProfiles` | Optional allowed profile names from host configuration |
| `onInputFailure` | Existing stage for timeout/no-match/playback failure in supported adapters |
| `action` | Registered `ICallWorkflowAction` executed after required authentication |
| `onActionSuccess`, `onActionFailure` | Mandatory existing outcome stages when `action` is present |
| `realtime` | `instructions`, `examples`, and `tools` arrays |
| `nlu` | `instructions` and `intents` (`name`, optional `description`, and `transition` label) |
| `scripted` | `ssml` (plain text or SSML), optional HTTPS `audioFile`, and digit-keyed `menu` |
| `transitions` | Edges with `to`, optional `label` (defaults to target), `when`, and `requires` |

`scripted.menu` values have an optional display `label` and required `transition`.
Menu and NLU `transition` fields refer to **edge labels**, not target stage IDs.
Labels are unique per stage, case-insensitively. Cycles are supported for menus;
automatic action chains have a bounded per-input transition count.

## Predicates

Each `requires` entry has `type`:

- `auth`: `level` is a defined caller-verification enum name.
- `state`: `key`, with optional `equals`, checks explicitly recorded data.
- `predicate`: `id` resolves a registered call-scoped predicate.

All can include `message`. Numeric assurance comparisons remain a compatibility
surface for legacy policies; required named authentication plans use actual method,
subject, and freshness evidence. Prefer those plans for sensitive actions.

## Migration

The previous `name` / `base` / `capabilities` / `scripted.dtmf` dialect is removed
from the shipped samples/schema. Convert explicitly rather than silently ignoring
unknown fields. Add failure routes to every authentication plan and register its
credential methods. Select explicit revisions when multiple versions coexist.
Persisted evidence without subject/timestamp metadata is not reusable verification.

No field implies an Azure deployment, unlimited capacity, or automatic socket
recovery. See the [runtime boundary and adoption notes](../README.md).
