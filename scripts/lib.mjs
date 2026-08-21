// Shared validation library for the solution manifest and publication brief.
//
// Kept dependency-light on purpose: only `yaml` (parsing) and `ajv` (JSON
// Schema validation) are required. All checks are pure functions that
// return a list of human-readable error strings, so both the CLI
// (scripts/validate.mjs) and the test suite (test/) can reuse them.

import fs from "node:fs";
import path from "node:path";
import { parse as parseYaml } from "yaml";
import Ajv from "ajv";

/**
 * Parse a YAML file, returning { data, error }. Never throws.
 */
export function readYamlFile(filePath) {
  let raw;
  try {
    raw = fs.readFileSync(filePath, "utf8");
  } catch (err) {
    return { data: null, error: `Could not read ${filePath}: ${err.message}` };
  }
  try {
    return { data: parseYaml(raw), error: null };
  } catch (err) {
    return { data: null, error: `Could not parse YAML in ${filePath}: ${err.message}` };
  }
}

export function readJsonFile(filePath) {
  let raw;
  try {
    raw = fs.readFileSync(filePath, "utf8");
  } catch (err) {
    return { data: null, error: `Could not read ${filePath}: ${err.message}` };
  }
  try {
    return { data: JSON.parse(raw), error: null };
  } catch (err) {
    return { data: null, error: `Could not parse JSON in ${filePath}: ${err.message}` };
  }
}

function newAjv() {
  return new Ajv({ allErrors: true, strict: false });
}

/**
 * Validate `data` against a JSON Schema object. Returns a list of
 * human-readable error strings (empty if valid).
 */
export function validateAgainstSchema(data, schema, label) {
  const ajv = newAjv();
  const validateFn = ajv.compile(schema);
  const valid = validateFn(data);
  if (valid) return [];
  return (validateFn.errors ?? []).map(
    (e) => `${label}: ${e.instancePath || "(root)"} ${e.message}`
  );
}

/**
 * Checks whether a path value is an acceptable repository-relative local
 * path: no leading "/", no ".." traversal segments, and not a URL.
 * Returns null if acceptable, or an error string describing the problem.
 */
export function checkLocalPathShape(value, label) {
  if (typeof value !== "string" || value.length === 0) {
    return `${label}: must be a non-empty string`;
  }
  if (value !== value.trim() || /[\u0000-\u001f]/.test(value)) {
    return `${label}: must not contain leading/trailing whitespace or control characters`;
  }
  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(value) || value.startsWith("//")) {
    return `${label}: must be a repository-relative local path, not a URL ("${value}")`;
  }
  if (
    path.isAbsolute(value) ||
    value.startsWith("/") ||
    value.startsWith("\\") ||
    /^[a-zA-Z]:[\\/]/.test(value)
  ) {
    return `${label}: must be repository-relative, not absolute ("${value}")`;
  }
  if (value.includes("\\")) {
    return `${label}: must use forward slashes so it is cross-platform ("${value}")`;
  }
  const firstHash = value.indexOf("#");
  const withoutFragment = firstHash === -1 ? value : value.slice(0, firstHash);
  const fragment = firstHash === -1 ? null : value.slice(firstHash + 1);
  if (!withoutFragment || fragment === "" || fragment?.includes("#")) {
    return `${label}: must contain a path and at most one non-empty fragment ("${value}")`;
  }
  if (/[*?[\]{}]/.test(withoutFragment)) {
    return `${label}: path must not contain glob metacharacters ("${value}")`;
  }
  const segments = withoutFragment.split("/");
  if (segments.some((segment) => segment === ".." || segment === "." || segment === "")) {
    return `${label}: must not use empty, current-, or parent-directory traversal segments ("${value}")`;
  }
  return null;
}

/**
 * Checks that a repository-relative local path (optionally with a
 * "#fragment") points at a file/directory that actually exists, relative
 * to `repoRoot`. Assumes checkLocalPathShape has already passed.
 */
export function checkLocalPathExists(value, repoRoot, label) {
  const withoutFragment = value.split("#", 1)[0];
  const root = path.resolve(repoRoot);
  const resolved = path.resolve(root, ...withoutFragment.split("/"));
  const relative = path.relative(root, resolved);
  if (relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    return `${label}: path resolves outside the repository: "${withoutFragment}"`;
  }
  if (!fs.existsSync(resolved)) {
    return `${label}: target does not exist: "${withoutFragment}"`;
  }
  const realRoot = fs.realpathSync(root);
  const realTarget = fs.realpathSync(resolved);
  const realRelative = path.relative(realRoot, realTarget);
  if (
    realRelative === ".." ||
    realRelative.startsWith(`..${path.sep}`) ||
    path.isAbsolute(realRelative)
  ) {
    return `${label}: target resolves outside the repository: "${withoutFragment}"`;
  }
  return null;
}

function githubHeadingSlug(value) {
  return value
    .toLowerCase()
    .replace(/<[^>]*>/g, "")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/[`~!@#$%^&*()+={}\[\]|\\:;"'<>,.?/]/g, "")
    .trim()
    .replace(/\s+/g, "-");
}

export function checkMarkdownFragmentExists(value, repoRoot, label) {
  const hashIndex = value.indexOf("#");
  if (hashIndex === -1) return null;
  const filePath = value.slice(0, hashIndex);
  let fragment = value.slice(hashIndex + 1);
  if (!filePath.toLowerCase().endsWith(".md")) {
    return `${label}: fragments may only target Markdown files`;
  }
  const resolvedFile = path.resolve(repoRoot, ...filePath.split("/"));
  if (!fs.statSync(resolvedFile).isFile()) {
    return `${label}: Markdown fragment target must be a file`;
  }
  try {
    fragment = decodeURIComponent(fragment);
  } catch {
    return `${label}: Markdown fragment is not valid URI encoding: "#${fragment}"`;
  }
  const headings = fs
    .readFileSync(resolvedFile, "utf8")
    .split(/\r?\n/)
    .filter((line) => /^#{1,6}\s+/.test(line))
    .map((line) => githubHeadingSlug(line.replace(/^#{1,6}\s+/, "")));
  const seen = new Map();
  const slugs = headings.map((slug) => {
    const count = seen.get(slug) ?? 0;
    seen.set(slug, count + 1);
    return count === 0 ? slug : `${slug}-${count}`;
  });
  return slugs.includes(fragment)
    ? null
    : `${label}: Markdown fragment does not exist: "#${fragment}"`;
}

const REQUIRED_MANIFEST_ENTRY_POINTS = [
  "overview",
  "architectureIndex",
  "adrIndex",
  "deployment",
  "runbooks",
  "observability",
  "evidence",
  "publicationBrief",
];

const OPTIONAL_MANIFEST_ENTRY_POINTS = ["implementation", "automation"];

function globToRegExp(glob) {
  let expression = "^";
  for (let index = 0; index < glob.length; index += 1) {
    const character = glob[index];
    if (character === "*" && glob[index + 1] === "*") {
      expression += ".*";
      index += 1;
    } else if (character === "*") {
      expression += "[^/]*";
    } else if (character === "?") {
      expression += "[^/]";
    } else {
      expression += /[.*+?^${}()|[\]\\]/.test(character)
        ? `\\${character}`
        : character;
    }
  }
  return new RegExp(`${expression}$`);
}

function exclusionCoversPath(exclusion, target) {
  const targetPath = target.split("#", 1)[0];
  if (typeof exclusion.path === "string" && exclusion.path) {
    return (
      targetPath === exclusion.path ||
      targetPath.startsWith(`${exclusion.path.replace(/\/+$/, "")}/`)
    );
  }
  return typeof exclusion.glob === "string"
    ? globToRegExp(exclusion.glob).test(targetPath)
    : false;
}

function validateExclusionGlob(glob, repoRoot, label) {
  const errors = [];
  if (typeof glob !== "string" || !glob) {
    return [`${label}: glob must be a non-empty string`];
  }
  if (/[\\[\]{}!#]/.test(glob)) {
    errors.push(`${label}: glob may use only forward-slash paths with "*" and "?" wildcards`);
    return errors;
  }
  const wildcardIndex = glob.search(/[*?]/);
  const literal = wildcardIndex === -1 ? glob : glob.slice(0, wildcardIndex);
  const prefix = wildcardIndex === -1
    ? literal
    : literal.endsWith("/")
      ? literal.replace(/\/+$/, "")
      : literal.includes("/")
        ? literal.slice(0, literal.lastIndexOf("/"))
        : "";
  if (!prefix) {
    errors.push(`${label}: glob must start with a safe repository-relative prefix`);
    return errors;
  }
  const shapeError = checkLocalPathShape(prefix, `${label} prefix`);
  if (shapeError) {
    errors.push(shapeError);
    return errors;
  }
  const existsError = checkLocalPathExists(prefix, repoRoot, `${label} prefix`);
  if (existsError) errors.push(existsError);
  return errors;
}

/**
 * Validate a parsed solution-manifest document: schema, then required
 * entry points exist and every non-null entry point (required or
 * optional) is a well-formed local path.
 */
export function validateManifestDocument(data, schema, repoRoot) {
  const errors = [];
  errors.push(...validateAgainstSchema(data, schema, "solution-manifest.yaml"));

  const entryPoints = (data && data.entryPoints) || {};
  for (const key of REQUIRED_MANIFEST_ENTRY_POINTS) {
    const value = entryPoints[key];
    const label = `solution-manifest.yaml entryPoints.${key}`;
    if (value === undefined || value === null) {
      errors.push(`${label}: is required and must not be null`);
      continue;
    }
    const shapeError = checkLocalPathShape(value, label);
    if (shapeError) {
      errors.push(shapeError);
      continue;
    }
    const existsError = checkLocalPathExists(value, repoRoot, label);
    if (existsError) errors.push(existsError);
    else {
      const fragmentError = checkMarkdownFragmentExists(value, repoRoot, label);
      if (fragmentError) errors.push(fragmentError);
    }
  }
  for (const key of OPTIONAL_MANIFEST_ENTRY_POINTS) {
    const value = entryPoints[key];
    if (value === undefined || value === null) continue;
    const label = `solution-manifest.yaml entryPoints.${key}`;
    const shapeError = checkLocalPathShape(value, label);
    if (shapeError) {
      errors.push(shapeError);
      continue;
    }
    const existsError = checkLocalPathExists(value, repoRoot, label);
    if (existsError) errors.push(existsError);
    else {
      const fragmentError = checkMarkdownFragmentExists(value, repoRoot, label);
      if (fragmentError) errors.push(fragmentError);
    }
  }
  const exclusions = Array.isArray(data?.synthesisExclusions)
    ? data.synthesisExclusions
    : [];
  const coveredEntryPoints = new Set();
  for (const [index, exclusion] of exclusions.entries()) {
    const label = `solution-manifest.yaml synthesisExclusions[${index}]`;
    if (typeof exclusion?.path === "string" && exclusion.path) {
      if (exclusion.path.includes("#")) {
        errors.push(`${label}.path: exclusions must target a file or directory, not a fragment`);
        continue;
      }
      const shapeError = checkLocalPathShape(exclusion.path, `${label}.path`);
      if (shapeError) errors.push(shapeError);
      else {
        const existsError = checkLocalPathExists(
          exclusion.path,
          repoRoot,
          `${label}.path`
        );
        if (existsError) errors.push(existsError);
      }
    } else if (typeof exclusion?.glob === "string" && exclusion.glob) {
      errors.push(...validateExclusionGlob(exclusion.glob, repoRoot, `${label}.glob`));
    }
    for (const [key, entryPoint] of Object.entries(entryPoints)) {
      if (
        typeof entryPoint === "string" &&
        (typeof exclusion?.path === "string" ||
          typeof exclusion?.glob === "string")
      ) {
        if (exclusionCoversPath(exclusion, entryPoint)) {
          coveredEntryPoints.add(key);
        }
      }
    }
  }
  const canonicalEntryPoints = REQUIRED_MANIFEST_ENTRY_POINTS.filter(
    (key) => typeof entryPoints[key] === "string"
  );
  if (
    canonicalEntryPoints.length > 0 &&
    canonicalEntryPoints.every((key) => coveredEntryPoints.has(key))
  ) {
    errors.push(
      "solution-manifest.yaml synthesisExclusions: exclusions cover all canonical entryPoints"
    );
  }
  return errors;
}

function validateReferencePath(value, repoRoot, label, errors) {
  const shapeError = checkLocalPathShape(value, label);
  if (shapeError) {
    errors.push(shapeError);
    return;
  }
  const existsError = checkLocalPathExists(value, repoRoot, label);
  if (existsError) errors.push(existsError);
  else {
    const fragmentError = checkMarkdownFragmentExists(value, repoRoot, label);
    if (fragmentError) errors.push(fragmentError);
  }
}

/**
 * Validate a parsed blog-brief document: schema, then well-formed (and,
 * where non-empty, existing) local paths for every candidate's path-list
 * fields.
 */
export function validateBlogBriefDocument(data, schema, repoRoot) {
  const errors = [];
  errors.push(...validateAgainstSchema(data, schema, "blog-brief.yaml"));

  const candidates = Array.isArray(data?.candidates) ? data.candidates : [];
  const ids = new Set();
  for (const [candidateIndex, candidate] of candidates.entries()) {
    if (!candidate || typeof candidate !== "object") continue;
    if (ids.has(candidate.id)) {
      errors.push(`blog-brief.yaml candidates[${candidateIndex}].id: duplicate candidate ID "${candidate.id}"`);
    }
    ids.add(candidate.id);
    for (const field of ["sourceArtifacts", "operations"]) {
      const values = candidate[field];
      if (!Array.isArray(values)) continue;
      values.forEach((value, referenceIndex) => {
        const label = `blog-brief.yaml candidates[${candidateIndex}].${field}[${referenceIndex}].path`;
        validateReferencePath(value?.path, repoRoot, label, errors);
      });
    }
    const canonicalAdrs = Array.isArray(candidate.canonicalAdrs)
      ? candidate.canonicalAdrs
      : [];
    for (const [adrIndex, adr] of canonicalAdrs.entries()) {
      if (adr?.path !== undefined) {
        validateReferencePath(adr.path, repoRoot, `blog-brief.yaml candidates[${candidateIndex}].canonicalAdrs[${adrIndex}].path`, errors);
      }
    }
    const evidenceEntries = Array.isArray(candidate.evidence)
      ? candidate.evidence
      : [];
    for (const [evidenceIndex, evidence] of evidenceEntries.entries()) {
      if (evidence?.path) {
        validateReferencePath(evidence.path, repoRoot, `blog-brief.yaml candidates[${candidateIndex}].evidence[${evidenceIndex}].path`, errors);
      }
    }
  }
  return errors;
}

export function validateContractDocuments(manifest, brief) {
  const errors = [];
  if (!manifest || !brief) return errors;
  if (manifest.solution?.id !== brief.solution?.id) {
    errors.push("solution-manifest.yaml and blog-brief.yaml: solution IDs must match");
  }
  const { owner, name } = manifest.repository ?? {};
  if (
    typeof owner === "string" &&
    owner &&
    typeof name === "string" &&
    name &&
    `${owner}/${name}` !== brief.solution?.repository
  ) {
    errors.push("solution-manifest.yaml and blog-brief.yaml: repository must match manifest owner/name");
  }
  if (
    manifest.repository?.visibility !== undefined &&
    manifest.repository.visibility !== brief.solution?.visibility
  ) {
    errors.push("solution-manifest.yaml and blog-brief.yaml: visibility must match");
  }
  return errors;
}

export function findUnresolvedPlaceholders(data, label) {
  const errors = [];
  function visit(value, currentPath) {
    if (typeof value === "string" && value.includes("REPLACE_ME")) {
      errors.push(`${label} ${currentPath}: unresolved REPLACE_ME value`);
    } else if (Array.isArray(value)) {
      value.forEach((item, index) => visit(item, `${currentPath}[${index}]`));
    } else if (value && typeof value === "object") {
      Object.entries(value).forEach(([key, item]) => visit(item, `${currentPath}.${key}`));
    }
  }
  visit(data, "");
  return errors;
}

const FRONTMATTER_PATTERN = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/;

/**
 * Extract and parse YAML frontmatter from a Markdown file's contents.
 * Returns { frontmatter, error }. `frontmatter` is null if no frontmatter
 * block is found or it fails to parse.
 */
export function parseFrontmatter(contents) {
  const match = contents.match(FRONTMATTER_PATTERN);
  if (!match) {
    return { frontmatter: null, error: "no YAML frontmatter block found" };
  }
  try {
    return { frontmatter: parseYaml(match[1]) ?? {}, error: null };
  } catch (err) {
    return { frontmatter: null, error: `invalid YAML frontmatter: ${err.message}` };
  }
}

/**
 * Validate the YAML frontmatter of an `.agent.md` file: requires
 * `description`.
 */
export function validateAgentFrontmatter(contents, label) {
  const { frontmatter, error } = parseFrontmatter(contents);
  if (error) return [`${label}: ${error}`];
  const errors = [];
  if (!frontmatter.description || typeof frontmatter.description !== "string") {
    errors.push(`${label}: frontmatter must include a non-empty "description"`);
  }
  if (frontmatter.name !== undefined && typeof frontmatter.name !== "string") {
    errors.push(`${label}: frontmatter "name" must be a string`);
  }
  if (
    frontmatter.tools !== undefined &&
    (!Array.isArray(frontmatter.tools) ||
      frontmatter.tools.some((tool) => typeof tool !== "string" || !tool))
  ) {
    errors.push(`${label}: frontmatter "tools" must be an array of non-empty strings`);
  }
  return errors;
}

/**
 * Validate the YAML frontmatter of an `.instructions.md` file: requires
 * `applyTo` (a glob string) and `description`.
 */
export function validateInstructionsFrontmatter(contents, label) {
  const { frontmatter, error } = parseFrontmatter(contents);
  if (error) return [`${label}: ${error}`];
  const errors = [];
  if (!frontmatter.applyTo || typeof frontmatter.applyTo !== "string") {
    errors.push(`${label}: frontmatter must include a non-empty "applyTo" glob`);
  }
  if (!frontmatter.description || typeof frontmatter.description !== "string") {
    errors.push(`${label}: frontmatter must include a non-empty "description"`);
  }
  if (
    Object.keys(frontmatter).some(
      (key) => !["applyTo", "description"].includes(key)
    )
  ) {
    errors.push(`${label}: frontmatter may contain only "applyTo" and "description"`);
  }
  return errors;
}
