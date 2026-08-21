# Publishing

How reviewed material from this repository reaches an external architecture blog — and what that process is explicitly not allowed to do.

## The contract

- This public repository is the **source of truth** for the accelerator artifacts. It never publishes a blog post.
- The blog repository (`colincmac/ctrlaltarchitect`) consumes metadata **at authoring time only**, while a human writes a post. Nothing here pushes, deploys, or opens pull requests there.
- [`blog-brief.yaml`](blog-brief.yaml) is **reviewed synthesis metadata**, not blog prose. It says *what could be written about, from which sources, with what evidence, and with which caveats*. It does not contain the article.
- ADRs remain canonical here. A published post may summarize a decision; it never becomes the record of that decision, and it never restates a status that the ADR file does not carry.

## Files

| File | Purpose |
| --- | --- |
| [`blog-brief.yaml`](blog-brief.yaml) | Candidate material mapped to the four blog formats, with `sourceArtifacts`, `canonicalAdrs`, operations, evidence, `evidenceGaps`, `detailsToGeneralize`, and `excludedMaterial`. |
| [`../solution-manifest.yaml`](../solution-manifest.yaml) | The machine-readable entry points a consumer starts from, and the paths excluded from synthesis. |
| [`blog-brief.schema.json`](blog-brief.schema.json) | Immutable canonical v1.0.0 schema for this brief. |
| [`../solution-manifest.schema.json`](../solution-manifest.schema.json) | Immutable canonical v1.0.0 schema for the manifest. |
| [`../evidence/README.md`](../evidence/README.md) | The measured / modeled / assumed / gap classification every candidate must respect. |

## The four formats

| Format | Answers | Typical spine |
| --- | --- | --- |
| `architectural-decision` | "Why this and not that?" | One ADR, its alternatives, and its consequences. |
| `large-scale-lessons` | "What breaks as this grows?" | The constraint that forces the design, stated as modeled unless measured. |
| `reference-architecture` | "What does the shape look like?" | An architecture view plus the decisions that produced it. |
| `field-note` | "What surprised us in practice?" | A narrow, concrete observation from work done in this repository. |

## Rules for anyone (or anything) updating the brief

1. **Never invent.** No customer context, no outcomes, no validation, no ADR history that is not in the ADR file.
2. **Carry the evidence class through.** A modeled scaling target is published as a design target, never as a result. Copy every `evidence` entry with `class: gap` into `evidenceGaps`; it may remain in both forms.
3. **Cite real paths.** Every source artifact, current ADR, operational artifact, and non-gap evidence entry must resolve in this repository; validation enforces this.
4. **Record canonical ADR provenance.** A real ADR uses `kind: current-record`, a path, and a lowercase `statusAsReviewed`. Preserve primary/supporting context in `note`. A reconstructed decision has only an ID, `kind: reconstructed`, and a provenance note.
5. **Generalize deliberately.** Tenant, subscription, resource-account, phone-number, and endpoint specifics get generalized before anything is written.
6. **Exclude, do not delete.** Cached vendor snapshots under `docs/resources/**` stay in the repository and stay out of synthesis.
7. **Re-review after ADR or architecture changes.** Use an ISO-8601 timestamp and reviewer where required. Pin `solution.ref` in a follow-up commit to the exact commit containing the reviewed artifact changes, so the final commit does not reference itself.

## Workflow

1. Someone updates architecture, ADRs, runbooks, monitoring, or evidence in the normal way.
2. The solution-artifact curator ([`.github/agents/solution-artifact-curator.agent.md`](../../.github/agents/solution-artifact-curator.agent.md)) keeps the manifest, architecture index, and evidence catalog in step with reality.
3. The blog brief generator ([`.github/agents/blog-brief-generator.agent.md`](../../.github/agents/blog-brief-generator.agent.md)) proposes brief updates. Its output is a proposal; a human reviews and merges it here.
4. An author working in the blog repository reads the brief, follows the links back to the canonical documents, and writes the post there.

## Validation

```shell
npm ci
npm test
npm run validate:initialized
```

Validates both documents against the canonical v1.0.0 schemas, checks that the brief and manifest describe the same solution and repository, resolves declared paths and fragments, enforces glob exclusions and ADR/evidence conditions, rejects retired aliases, and checks agent/instruction frontmatter. General Markdown links still require review.
