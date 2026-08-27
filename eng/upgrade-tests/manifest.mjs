import { readFileSync } from "node:fs";
import { isAbsolute, relative, resolve, sep } from "node:path";

export const REQUIRED_SCENARIOS = new Set([
  "workspaces",
  "permissions",
  "templates",
  "components",
  "choice-revisions",
  "content-versions",
  "schedules",
  "media",
  "webhooks",
  "audit",
  "authentication",
  "provenance",
]);

const MANIFEST_KEYS = ["schemaVersion", "baseline", "requiredFiles", "requiredScenarios", "expectedDataFile"];
const BASELINE_KEYS = ["version", "sourceSha", "apiImage", "postgresImage", "minioImage"];
const IMAGE_KEYS = ["repository", "tag", "digest", "platform"];
const REQUIRED_FILE_NAMES = ["database.sql", "expected.json", "manifest.json"];
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const SOURCE_SHA = /^[0-9a-f]{40}$/;
const IMAGE_REPOSITORY = /^[a-z0-9]+(?:[._/-][a-z0-9]+)*$/;
const IMAGE_TAG = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

function assert(condition, message) {
  if (!condition) throw new Error(`Invalid fixture manifest: ${message}`);
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function assertExactKeys(value, expectedKeys, name) {
  assert(isPlainObject(value), `${name} must be an object.`);
  const keys = Object.keys(value).sort();
  assert(keys.length === expectedKeys.length && keys.every((key, index) => key === [...expectedKeys].sort()[index]), `${name} has unknown or missing properties.`);
}

function freeze(value) {
  if (Array.isArray(value)) {
    for (const item of value) freeze(item);
  } else if (isPlainObject(value)) {
    for (const item of Object.values(value)) freeze(item);
  }
  return Object.freeze(value);
}

function canonicalFixturePath(file, fixtureDirectory, name) {
  assert(typeof file === "string" && file.length > 0, `${name} must be a non-empty string.`);
  assert(!file.includes("\\"), `${name} must use forward slashes.`);
  assert(!isAbsolute(file), `${name} must be fixture-relative.`);
  assert(file.split("/").every((part) => part.length > 0 && part !== "." && part !== ".."), `${name} must use canonical path segments.`);

  const resolved = resolve(fixtureDirectory, file);
  const fixtureRelative = relative(fixtureDirectory, resolved);
  assert(!fixtureRelative.startsWith("..") && !isAbsolute(fixtureRelative), `${name} escapes the fixture directory.`);
  assert(!fixtureRelative.split(sep).includes(".."), `${name} escapes the fixture directory.`);
  return file;
}

function validateImmutableImage(image, name) {
  assertExactKeys(image, IMAGE_KEYS, name);
  assert(typeof image.repository === "string" && IMAGE_REPOSITORY.test(image.repository), `${name}.repository must be a canonical repository.`);
  assert(typeof image.tag === "string" && IMAGE_TAG.test(image.tag), `${name}.tag must be a canonical tag.`);
  assert(typeof image.digest === "string" && DIGEST.test(image.digest), `${name}.digest must be a lowercase sha256 digest.`);
  assert(image.platform === "linux/amd64", `${name}.platform must be linux/amd64.`);
  return {
    repository: image.repository,
    tag: image.tag,
    digest: image.digest,
    platform: image.platform,
  };
}

/**
 * @typedef {{repository:string, tag:string, digest:string, platform:"linux/amd64"}} ImmutableImage
 * @typedef {{schemaVersion:1, baseline:{version:"0.1.3", sourceSha:string, apiImage:ImmutableImage, postgresImage:ImmutableImage, minioImage:ImmutableImage}, requiredFiles:string[], requiredScenarios:string[], expectedDataFile:"expected.json"}} FixtureManifest
 */

/**
 * Parses and validates the fixture manifest in a fixture directory.
 * @param {string} fixtureDirectory
 * @returns {FixtureManifest}
 */
export function loadFixtureManifest(fixtureDirectory) {
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(resolve(fixtureDirectory, "manifest.json"), "utf8"));
  } catch {
    throw new Error("Invalid fixture manifest: manifest.json must contain valid JSON.");
  }
  return validateFixtureManifest(parsed, fixtureDirectory);
}

/**
 * Validates an untrusted fixture manifest and returns a deeply frozen copy.
 * @param {unknown} manifest
 * @param {string} fixtureDirectory
 * @returns {FixtureManifest}
 */
export function validateFixtureManifest(manifest, fixtureDirectory) {
  assert(typeof fixtureDirectory === "string" && fixtureDirectory.length > 0, "fixtureDirectory must be a non-empty path.");
  assertExactKeys(manifest, MANIFEST_KEYS, "manifest");
  assert(manifest.schemaVersion === 1, "schemaVersion must be 1.");
  assertExactKeys(manifest.baseline, BASELINE_KEYS, "baseline");
  assert(manifest.baseline.version === "0.1.3", "baseline.version must be canonical SemVer 0.1.3.");
  assert(typeof manifest.baseline.sourceSha === "string" && SOURCE_SHA.test(manifest.baseline.sourceSha), "baseline.sourceSha must be a lowercase 40-character SHA.");

  const baseline = {
    version: manifest.baseline.version,
    sourceSha: manifest.baseline.sourceSha,
    apiImage: validateImmutableImage(manifest.baseline.apiImage, "baseline.apiImage"),
    postgresImage: validateImmutableImage(manifest.baseline.postgresImage, "baseline.postgresImage"),
    minioImage: validateImmutableImage(manifest.baseline.minioImage, "baseline.minioImage"),
  };
  assert(baseline.apiImage.tag === baseline.version, "baseline.apiImage.tag must equal baseline.version.");

  assert(Array.isArray(manifest.requiredFiles) && manifest.requiredFiles.length > 0, "requiredFiles must be a non-empty array.");
  const requiredFiles = manifest.requiredFiles.map((file, index) => canonicalFixturePath(file, fixtureDirectory, `requiredFiles[${index}]`));
  assert(requiredFiles.every((file, index) => index === 0 || requiredFiles[index - 1] < file), "requiredFiles must be sorted and unique.");
  for (const file of REQUIRED_FILE_NAMES) assert(requiredFiles.includes(file), `requiredFiles must include ${file}.`);

  assert(Array.isArray(manifest.requiredScenarios), "requiredScenarios must be an array.");
  assert(manifest.requiredScenarios.length === REQUIRED_SCENARIOS.size, "requiredScenarios must contain every required scenario exactly once.");
  assert(manifest.requiredScenarios.every((scenario) => typeof scenario === "string" && REQUIRED_SCENARIOS.has(scenario)), "requiredScenarios contains an unknown scenario.");
  assert(new Set(manifest.requiredScenarios).size === manifest.requiredScenarios.length, "requiredScenarios must be unique.");

  assert(manifest.expectedDataFile === "expected.json", "expectedDataFile must be expected.json.");
  assert(requiredFiles.includes(manifest.expectedDataFile), "expectedDataFile must be listed in requiredFiles.");

  return freeze({
    schemaVersion: 1,
    baseline,
    requiredFiles: [...requiredFiles],
    requiredScenarios: [...manifest.requiredScenarios],
    expectedDataFile: manifest.expectedDataFile,
  });
}
