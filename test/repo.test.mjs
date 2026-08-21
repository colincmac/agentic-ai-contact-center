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

test("generated repository initialization fails with placeholders and passes after replacement", () => {
  const fixtureRoot = fs.mkdtempSync(
    path.join(os.tmpdir(), "solution-artifact-initialization-")
  );
  try {
    fs.cpSync(path.join(repoRoot, "docs"), path.join(fixtureRoot, "docs"), {
      recursive: true,
    });
    fs.cpSync(path.join(repoRoot, ".github"), path.join(fixtureRoot, ".github"), {
      recursive: true,
    });

    const unresolved = runValidation(fixtureRoot, { requireInitialized: true });
    assert.ok(unresolved.some((error) => error.includes("unresolved REPLACE_ME")));

    for (const relativePath of [
      path.join("docs", "solution-manifest.yaml"),
      path.join("docs", "publishing", "blog-brief.yaml"),
    ]) {
      const filePath = path.join(fixtureRoot, relativePath);
      const initialized = fs.readFileSync(filePath, "utf8").replaceAll(
        "REPLACE_ME",
        "example"
      );
      fs.writeFileSync(filePath, initialized);
    }

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
