import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import fs from "node:fs";
import os from "node:os";
import { fileURLToPath } from "node:url";
import {
  runValidation,
  validateRepositoryConformance,
} from "../scripts/validate.mjs";
import { parse as parseYaml, stringify as stringifyYaml } from "yaml";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..");

test("the committed solution-manifest.yaml and blog-brief.yaml validate cleanly", () => {
  const errors = runValidation(repoRoot);
  assert.deepEqual(errors, []);
});

test("initialized repository rejects reintroduced placeholders", () => {
  const fixtureRoot = fs.mkdtempSync(
    path.join(os.tmpdir(), "solution-artifact-initialization-")
  );
  try {
    for (const relativePath of [".github", "code", "docs", "infra", "scripts", "test"]) {
      fs.cpSync(path.join(repoRoot, relativePath), path.join(fixtureRoot, relativePath), {
        recursive: true,
      });
    }

    const manifestPath = path.join(fixtureRoot, "docs", "solution-manifest.yaml");
    const manifestWithPlaceholder = fs.readFileSync(manifestPath, "utf8").replace(
      'name: "Agentic AI Contact Center Accelerator"',
      'name: "REPLACE_ME"'
    );
    fs.writeFileSync(manifestPath, manifestWithPlaceholder);

    const unresolved = runValidation(fixtureRoot, { requireInitialized: true });
    assert.ok(unresolved.some((error) => error.includes("unresolved REPLACE_ME")));

    const initialized = fs.readFileSync(manifestPath, "utf8").replaceAll(
      "REPLACE_ME",
      "Agentic AI Contact Center Accelerator"
    );
    fs.writeFileSync(manifestPath, initialized);

    assert.deepEqual(
      runValidation(fixtureRoot, { requireInitialized: true }),
      []
    );
    assert.deepEqual(validateRepositoryConformance(fixtureRoot), []);

    // Conformance must use this repository's canonical schemas, not a schema
    // weakened inside the external target.
    fs.writeFileSync(
      path.join(fixtureRoot, "docs", "publishing", "blog-brief.schema.json"),
      "{}\n"
    );
    const briefPath = path.join(
      fixtureRoot,
      "docs",
      "publishing",
      "blog-brief.yaml"
    );
    const brief = parseYaml(fs.readFileSync(briefPath, "utf8"));
    brief.candidates[0].unexpectedMetadata = true;
    fs.writeFileSync(briefPath, stringifyYaml(brief));
    assert.ok(
      validateRepositoryConformance(fixtureRoot)
        .some((error) => error.includes("additional properties"))
    );
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});
