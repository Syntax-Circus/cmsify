import { createHash } from "node:crypto";
import { lstat, readdir, readFile, writeFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

const CHECKSUM_FILE = "SHA256SUMS";
const CHECKSUM_LINE = /^([0-9a-f]{64})  (.+)$/;

function fail(message) {
  throw new Error(message);
}

function normalizeRelativePath(value) {
  return value.replaceAll("\\", "/");
}

function safeFixturePath(fixtureDirectory, file, name = "fixture path") {
  if (typeof file !== "string" || file.length === 0) fail(`${name} must be a non-empty fixture-relative path.`);
  if (file.includes("\\") || isAbsolute(file) || file.split("/").some((part) => part === "." || part === ".." || part.length === 0)) {
    fail(`${name} must be a canonical fixture-relative path: ${file}.`);
  }
  const resolved = resolve(fixtureDirectory, file);
  const fixtureRelative = relative(fixtureDirectory, resolved);
  if (fixtureRelative.startsWith("..") || isAbsolute(fixtureRelative) || fixtureRelative.split(sep).includes("..")) {
    fail(`${name} escapes the fixture directory.`);
  }
  return { file, resolved };
}

async function regularFile(fixtureDirectory, file) {
  const { resolved } = safeFixturePath(fixtureDirectory, file);
  let stat;
  try {
    stat = await lstat(resolved);
  } catch {
    fail(`Missing fixture payload: ${file}.`);
  }
  if (stat.isSymbolicLink()) fail(`Fixture payload must not be a symbolic link: ${file}.`);
  if (!stat.isFile()) fail(`Fixture payload must be a regular file: ${file}.`);
  return resolved;
}

async function walkFixtureFiles(fixtureDirectory, directory = fixtureDirectory) {
  let entries;
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch {
    fail("Fixture directory cannot be read.");
  }

  const files = [];
  for (const entry of entries) {
    const entryPath = resolve(directory, entry.name);
    const entryRelative = normalizeRelativePath(relative(fixtureDirectory, entryPath));
    if (entry.isSymbolicLink()) fail(`Fixture must not contain symbolic links: ${entryRelative}.`);
    if (entry.isDirectory()) {
      files.push(...await walkFixtureFiles(fixtureDirectory, entryPath));
    } else if (entry.isFile()) {
      files.push(entryRelative);
    } else {
      fail(`Fixture contains unsupported filesystem entry: ${entryRelative}.`);
    }
  }
  return files.sort();
}

function parseChecksums(text, fixtureDirectory) {
  const checksums = new Map();
  const lines = text.split("\n");
  if (lines.at(-1) === "") lines.pop();
  if (lines.length === 0) fail("SHA256SUMS must contain at least one entry.");

  for (const line of lines) {
    const match = CHECKSUM_LINE.exec(line);
    if (!match) fail(`SHA256SUMS entry must use lowercase SHA-256 and exactly two spaces: ${line}.`);
    const [, digest, file] = match;
    safeFixturePath(fixtureDirectory, file, "SHA256SUMS entry");
    if (file === CHECKSUM_FILE) fail("SHA256SUMS must not checksum itself.");
    if (checksums.has(file)) fail(`SHA256SUMS contains duplicate entry: ${file}.`);
    checksums.set(file, digest);
  }
  return checksums;
}

function fixtureManifestFiles(manifest) {
  if (!manifest || !Array.isArray(manifest.requiredFiles)) fail("Fixture manifest must declare requiredFiles.");
  return new Set(manifest.requiredFiles);
}

/**
 * Writes canonical SHA256SUMS entries for the supplied fixture-relative files.
 * @param {string} fixtureDirectory
 * @param {string[]} relativeFiles
 * @returns {Promise<string>}
 */
export async function writeFixtureChecksums(fixtureDirectory, relativeFiles) {
  if (!Array.isArray(relativeFiles)) fail("relativeFiles must be an array.");
  const files = [...new Set(relativeFiles.map((file) => safeFixturePath(fixtureDirectory, file).file))]
    .filter((file) => file !== CHECKSUM_FILE)
    .sort();
  if (files.length !== relativeFiles.filter((file) => file !== CHECKSUM_FILE).length) fail("relativeFiles must be unique.");

  const lines = [];
  for (const file of files) {
    const path = await regularFile(fixtureDirectory, file);
    const digest = createHash("sha256").update(await readFile(path)).digest("hex");
    lines.push(`${digest}  ${file}`);
  }
  const text = lines.length === 0 ? "" : `${lines.join("\n")}\n`;
  await writeFile(resolve(fixtureDirectory, CHECKSUM_FILE), text, "utf8");
  return text;
}

/**
 * Verifies that the fixture's manifest, filesystem inventory, and SHA256SUMS agree.
 * @param {string} fixtureDirectory
 * @param {{requiredFiles:string[]}} manifest
 * @returns {Promise<Map<string, string>>}
 */
export async function verifyFixtureChecksums(fixtureDirectory, manifest) {
  const expectedFiles = fixtureManifestFiles(manifest);
  for (const file of expectedFiles) safeFixturePath(fixtureDirectory, file, "manifest requiredFile");

  const actualFiles = new Set((await walkFixtureFiles(fixtureDirectory)).filter((file) => file !== CHECKSUM_FILE));
  const errors = [];
  for (const file of expectedFiles) if (!actualFiles.has(file)) errors.push(`Missing fixture payload: ${file}.`);
  for (const file of actualFiles) if (!expectedFiles.has(file)) errors.push(`Unlisted fixture payload: ${file}.`);

  let checksumText;
  try {
    checksumText = await readFile(resolve(fixtureDirectory, CHECKSUM_FILE), "utf8");
  } catch {
    errors.push("Fixture must contain SHA256SUMS.");
  }

  let declared = new Map();
  if (checksumText !== undefined) {
    try {
      declared = parseChecksums(checksumText, fixtureDirectory);
    } catch (error) {
      errors.push(error.message);
    }
  }

  for (const file of actualFiles) if (!declared.has(file)) errors.push(`SHA256SUMS omits fixture payload: ${file}.`);
  for (const file of declared.keys()) if (!actualFiles.has(file)) errors.push(`SHA256SUMS contains extra entry: ${file}.`);
  if (errors.length > 0) fail(errors.join("\n"));

  const verified = new Map();
  for (const file of [...actualFiles].sort()) {
    const path = await regularFile(fixtureDirectory, file);
    const actual = createHash("sha256").update(await readFile(path)).digest("hex");
    if (declared.get(file) !== actual) errors.push(`Checksum mismatch for ${file}.`);
    else verified.set(file, actual);
  }
  if (errors.length > 0) fail(errors.join("\n"));
  return verified;
}
