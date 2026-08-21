import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import {
  parseFrontmatter,
  validateAgentFrontmatter,
  validateInstructionsFrontmatter,
} from "../scripts/lib.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..");

test("parseFrontmatter extracts a YAML frontmatter block", () => {
  const contents = "---\nname: Example\ndescription: Example agent.\n---\n\nBody.\n";
  const { frontmatter, error } = parseFrontmatter(contents);
  assert.equal(error, null);
  assert.equal(frontmatter.name, "Example");
});

test("parseFrontmatter errors when no frontmatter block is present", () => {
  const { frontmatter, error } = parseFrontmatter("# Just a heading\n");
  assert.equal(frontmatter, null);
  assert.match(error, /no YAML frontmatter/);
});

test("validateAgentFrontmatter requires a description", () => {
  const errors = validateAgentFrontmatter("---\nname: Example\n---\nBody.\n", "example.agent.md");
  assert.ok(errors.some((e) => e.includes("description")));
});

test("validateInstructionsFrontmatter requires applyTo and description", () => {
  const errors = validateInstructionsFrontmatter(
    "---\nname: Example\n---\nBody.\n",
    "example.instructions.md"
  );
  assert.ok(errors.some((e) => e.includes("applyTo")));
  assert.ok(errors.some((e) => e.includes("description")));
});

test("every committed .agent.md file has valid frontmatter", () => {
  const agentsDir = path.join(repoRoot, ".github", "agents");
  const files = fs
    .readdirSync(agentsDir)
    .filter((name) => name.endsWith(".agent.md"));
  assert.ok(files.length > 0, "expected at least one .agent.md file");
  for (const file of files) {
    const contents = fs.readFileSync(path.join(agentsDir, file), "utf8");
    const errors = validateAgentFrontmatter(contents, file);
    assert.deepEqual(errors, [], `${file}: ${errors.join(", ")}`);
  }
});

test("every committed .instructions.md file has valid frontmatter", () => {
  const instructionsDir = path.join(repoRoot, ".github", "instructions");
  const files = fs
    .readdirSync(instructionsDir)
    .filter((name) => name.endsWith(".instructions.md"));
  assert.ok(files.length > 0, "expected at least one .instructions.md file");
  for (const file of files) {
    const contents = fs.readFileSync(path.join(instructionsDir, file), "utf8");
    const errors = validateInstructionsFrontmatter(contents, file);
    assert.deepEqual(errors, [], `${file}: ${errors.join(", ")}`);
  }
});
