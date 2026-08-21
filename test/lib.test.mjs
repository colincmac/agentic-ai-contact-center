import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  readYamlFile,
  readJsonFile,
  validateManifestDocument,
  validateBlogBriefDocument,
  validateContractDocuments,
  checkLocalPathShape,
} from "../scripts/lib.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..");
const fixturePath = path.join(__dirname, "fixtures", "contact-center.yaml");
const { data: fixture } = readYamlFile(fixturePath);
const { data: manifestSchema } = readJsonFile(path.join(repoRoot, "docs", "solution-manifest.schema.json"));
const { data: briefSchema } = readJsonFile(path.join(repoRoot, "docs", "publishing", "blog-brief.schema.json"));

function copy() {
  return structuredClone(fixture);
}

test("rich multi-candidate contact-center fixture validates", () => {
  const { manifest, brief } = copy();
  assert.deepEqual(validateManifestDocument(manifest, manifestSchema, repoRoot), []);
  assert.deepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
  assert.deepEqual(validateContractDocuments(manifest, brief), []);
});

test("entry point fragments use GitHub heading slugs", () => {
  const { manifest } = copy();
  manifest.entryPoints.deployment = "docs/README.md#missing-heading";
  assert.ok(validateManifestDocument(manifest, manifestSchema, repoRoot).some((error) => error.includes("Markdown fragment")));
});

test("cross-document solution ID, repository, and visibility mismatches fail", () => {
  const { manifest, brief } = copy();
  brief.solution.id = "other";
  brief.solution.repository = "other/repository";
  brief.solution.visibility = "public";
  const errors = validateContractDocuments(manifest, brief);
  assert.equal(errors.length, 3);
  assert.ok(errors.some((error) => error.includes("solution IDs")));
  assert.ok(errors.some((error) => error.includes("repository")));
  assert.ok(errors.some((error) => error.includes("visibility")));
});

test("incomplete repository metadata does not produce a duplicate mismatch error", () => {
  const { manifest, brief } = copy();
  delete manifest.repository.owner;
  assert.deepEqual(validateContractDocuments(manifest, brief), []);
});

test("a current-record ADR with a path and valid status validates", () => {
  const { brief } = copy();
  brief.candidates[0].canonicalAdrs = [{
    id: "0001",
    kind: "current-record",
    path: "docs/adr/README.md",
    statusAsReviewed: "accepted",
  }];
  assert.deepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
});

test("a reconstructed decision with only provenance validates", () => {
  const { brief } = copy();
  brief.candidates[0].canonicalAdrs = [{
    id: "historical-routing-decision",
    kind: "reconstructed",
    note: "Reconstructed from the architecture history; no contemporaneous ADR exists.",
  }];
  assert.deepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
});

test("a reconstructed decision must not contain path", () => {
  const { brief } = copy();
  brief.candidates[0].canonicalAdrs = [{
    id: "historical-routing-decision",
    kind: "reconstructed",
    note: "Reconstructed from architecture history.",
    path: "docs/adr/README.md",
  }];
  assert.notDeepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
});

test("a reconstructed decision must not contain statusAsReviewed", () => {
  const { brief } = copy();
  brief.candidates[0].canonicalAdrs = [{
    id: "historical-routing-decision",
    kind: "reconstructed",
    note: "Reconstructed from architecture history.",
    statusAsReviewed: "accepted",
  }];
  assert.notDeepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
});

test("a current-record ADR requires a path", () => {
  const { brief } = copy();
  delete brief.candidates[0].canonicalAdrs[0].path;
  assert.notDeepEqual(validateBlogBriefDocument(brief, briefSchema, repoRoot), []);
});

test("a current-record ADR requires a valid statusAsReviewed", () => {
  const missing = copy().brief;
  delete missing.candidates[0].canonicalAdrs[0].statusAsReviewed;
  assert.notDeepEqual(validateBlogBriefDocument(missing, briefSchema, repoRoot), []);

  const invalid = copy().brief;
  invalid.candidates[0].canonicalAdrs[0].statusAsReviewed = "current";
  assert.notDeepEqual(validateBlogBriefDocument(invalid, briefSchema, repoRoot), []);
});

test("invalid evidence classes fail", () => {
  const { brief } = copy();
  brief.candidates[0].evidence[0].class = "assumption";
  const errors = validateBlogBriefDocument(brief, briefSchema, repoRoot);
  assert.ok(errors.length >= 1);
});

test("unsafe candidate paths and exclusion globs fail", () => {
  const { manifest, brief } = copy();
  manifest.synthesisExclusions = [{
    glob: "../**",
    reason: "Unsafe traversal.",
  }];
  brief.candidates[0].sourceArtifacts[0].path = "../private.md";
  const errors = [
    ...validateManifestDocument(manifest, manifestSchema, repoRoot),
    ...validateBlogBriefDocument(brief, briefSchema, repoRoot),
  ];
  assert.ok(errors.some((error) => error.includes("traversal")));

  manifest.synthesisExclusions = [{
    glob: "**/*.md",
    reason: "Missing safe prefix.",
  }];
  assert.ok(
    validateManifestDocument(manifest, manifestSchema, repoRoot)
      .some((error) => error.includes("safe repository-relative prefix"))
  );

  manifest.synthesisExclusions = [{
    glob: "docs/not-present/**",
    reason: "Missing prefix.",
  }];
  assert.ok(
    validateManifestDocument(manifest, manifestSchema, repoRoot)
      .some((error) => error.includes("target does not exist"))
  );
});

test("starter manifest leaves canonical entry points available for synthesis", () => {
  const { data: starter } = readYamlFile(path.join(repoRoot, "docs", "solution-manifest.yaml"));
  assert.deepEqual(starter.synthesisExclusions, []);
  assert.deepEqual(validateManifestDocument(starter, manifestSchema, repoRoot), []);

  starter.synthesisExclusions = [{
    glob: "docs/**",
    reason: "Overly broad example.",
  }];
  assert.ok(
    validateManifestDocument(starter, manifestSchema, repoRoot)
      .some((error) => error.includes("cover all canonical entryPoints"))
  );
});

test("duplicate candidate IDs fail", () => {
  const { brief } = copy();
  brief.candidates[1].id = brief.candidates[0].id;
  assert.ok(validateBlogBriefDocument(brief, briefSchema, repoRoot).some((error) => error.includes("duplicate candidate ID")));
});

test("candidate paths must exist and Markdown fragments must resolve", () => {
  const missing = copy().brief;
  missing.candidates[0].sourceArtifacts[0].path = "docs/missing.md";
  assert.ok(
    validateBlogBriefDocument(missing, briefSchema, repoRoot)
      .some((error) => error.includes("target does not exist"))
  );

  const brokenFragment = copy().brief;
  brokenFragment.candidates[0].sourceArtifacts[0].path =
    "docs/README.md#missing-heading";
  assert.ok(
    validateBlogBriefDocument(brokenFragment, briefSchema, repoRoot)
      .some((error) => error.includes("Markdown fragment"))
  );
});

test("unexpected properties are rejected outside extensions", () => {
  const { manifest, brief } = copy();
  manifest.unexpected = true;
  brief.candidates[0].unexpectedMetadata = true;
  const errors = [
    ...validateManifestDocument(manifest, manifestSchema, repoRoot),
    ...validateBlogBriefDocument(brief, briefSchema, repoRoot),
  ];
  assert.ok(errors.filter((error) => error.includes("additional properties")).length >= 2);

  const extended = copy();
  extended.manifest.extensions = { organizationMetadata: { ownerTeam: "architecture" } };
  extended.brief.extensions = { publicationMetadata: { channel: "external" } };
  assert.deepEqual(validateManifestDocument(extended.manifest, manifestSchema, repoRoot), []);
  assert.deepEqual(validateBlogBriefDocument(extended.brief, briefSchema, repoRoot), []);
});

test("local paths reject absolute paths, traversal, and URLs", () => {
  for (const value of [
    "/etc/passwd",
    "C:\\private.md",
    "../private.md",
    "https://example.test/a",
    "docs\\README.md",
    "#overview",
    "docs/*.md",
  ]) {
    assert.notEqual(checkLocalPathShape(value, "path"), null);
  }
});
