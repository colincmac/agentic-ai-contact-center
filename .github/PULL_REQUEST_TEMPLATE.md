## Summary

REPLACE_ME: what this PR does and why.

## Review checklist

- [ ] **Architecture impact:** Does this change system shape, a quality
      attribute, or a stated constraint? If yes, the relevant
      [architecture view](../docs/architecture/README.md) is updated.
- [ ] **ADR impact:** Is this change consequential and hard to reverse? If
      yes, a new or updated [ADR](../docs/adr/README.md) is included. (Not
      every change needs one — see [`CONTRIBUTING.md`](../CONTRIBUTING.md).)
- [ ] **Operational changes:** Does this change how an operational
      condition is diagnosed or mitigated? If yes, the relevant
      [runbook](../docs/runbooks/README.md) is updated.
- [ ] **Evidence:** Does this change rely on or produce a claim? If yes, use
      only `measured`, `modeled`, `assumed`, or `gap` in the
      [evidence record](../docs/evidence/README.md), with limitations stated
      honestly.
- [ ] **Security/privacy:** No credentials, tenant/subscription IDs,
      customer names, private URLs, or sensitive data are introduced.
- [ ] **Publication metadata:** Does this change require an update to
      [`docs/publishing/blog-brief.yaml`](../docs/publishing/README.md)
      (new content to propose, or content that must now be excluded)? If
      yes, a human reviewer has checked it.
- [ ] **ADR provenance:** Every publication `current-record` has `path` and
      `statusAsReviewed`; every `reconstructed` decision has a provenance
      `note` and neither of those fields.

## Validation

- [ ] `npm ci && npm test` passes locally.
- [ ] `npm run validate:initialized` passes for a repository created from this
      template (the canonical template intentionally retains `REPLACE_ME`).
