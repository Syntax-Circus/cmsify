#!/usr/bin/env node
import { createHash, randomBytes } from "node:crypto";
import {
  closeSync,
  fstatSync,
  lstatSync,
  mkdtempSync,
  openSync,
  readFileSync,
  readSync,
  realpathSync,
  rmSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, parse, relative, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { runProcess } from "../../eng/upgrade-tests/process.mjs";

export const SKOPEO_IMAGE = "quay.io/skopeo/stable:v1.22.2@sha256:0b98d4296bfd35680c09fd40a5bff17b8569715258a4bee0a7ae3ca500eaaece";
export const LOADER_CONTRACT = Object.freeze({
  schema: "cmsify.oci-loader.v1",
  skopeoImage: SKOPEO_IMAGE,
  transport: "offline-docker-archive",
});

const FLAGS = Object.freeze({
  "--archive": "archive",
  "--manifest": "manifest",
  "--kind": "kind",
  "--version": "version",
});
const SEMVER = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;
const DOCKER_TAG = /^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/;
const CANONICAL_REF = /^docker\.io\/syntaxcircus\/cmsify-(?:api|admin):[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/;
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json";
const OCI_CONFIG = "application/vnd.oci.image.config.v1+json";
const LAYER_MEDIA_TYPES = new Set([
  "application/vnd.oci.image.layer.v1.tar",
  "application/vnd.oci.image.layer.v1.tar+gzip",
  "application/vnd.oci.image.layer.v1.tar+zstd",
  "application/vnd.oci.image.layer.nondistributable.v1.tar",
  "application/vnd.oci.image.layer.nondistributable.v1.tar+gzip",
  "application/vnd.oci.image.layer.nondistributable.v1.tar+zstd",
  "application/vnd.docker.image.rootfs.diff.tar",
  "application/vnd.docker.image.rootfs.diff.tar.gzip",
  "application/vnd.docker.image.rootfs.foreign.diff.tar",
  "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip",
]);
const MAX_JSON_BYTES = 1024 * 1024;
const MAX_DOCKER_ARCHIVE_BYTES = 8 * 1024 * 1024 * 1024;
const PROCESS_TIMEOUT_MS = 2 * 60 * 1000;
const LABEL_OWNER = "io.syntaxcircus.cmsify.oci-loader=true";
const LABEL_RUN = "io.syntaxcircus.cmsify.oci-loader-run";

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function normalizedPath(path) {
  const withoutLongPrefix = path.replace(/^\\\\\?\\/, "").replaceAll("\\", "/");
  return process.platform === "win32" ? withoutLongPrefix.toLowerCase() : withoutLongPrefix;
}

function assertRegularNonLinkFile(input, label) {
  assert(typeof input === "string" && input.length > 0, `${label} path is required.`);
  const absolute = resolve(input);
  const root = parse(absolute).root;
  let current = root;
  for (const segment of relative(root, absolute).split(/[\\/]+/).filter(Boolean)) {
    current = resolve(current, segment);
    const entry = lstatSync(current);
    assert(!entry.isSymbolicLink(), `${label} path must not contain a link or reparse point.`);
  }
  const entry = lstatSync(absolute);
  assert(entry.isFile() && !entry.isSymbolicLink(), `${label} path must be a regular non-link file.`);
  assert(normalizedPath(realpathSync.native(absolute)) === normalizedPath(absolute), `${label} path must not resolve through a link or reparse point.`);
  assert(!absolute.includes(","), `${label} path must not contain a comma.`);
  return { path: absolute, size: entry.size, links: entry.nlink };
}

function assertRegularNonLinkDirectory(input, label) {
  assert(typeof input === "string" && input.length > 0, `${label} path is required.`);
  const absolute = resolve(input);
  const root = parse(absolute).root;
  let current = root;
  for (const segment of relative(root, absolute).split(/[\\/]+/).filter(Boolean)) {
    current = resolve(current, segment);
    const entry = lstatSync(current);
    assert(!entry.isSymbolicLink(), `${label} path must not contain a link or reparse point.`);
  }
  const entry = lstatSync(absolute);
  assert(entry.isDirectory() && !entry.isSymbolicLink(), `${label} path must be a regular non-link directory.`);
  assert(normalizedPath(realpathSync.native(absolute)) === normalizedPath(absolute), `${label} path must not resolve through a link or reparse point.`);
  assert(!absolute.includes(","), `${label} path must not contain a comma.`);
  return absolute;
}

function scratchRootPathForPrefix(input, prefix) {
  assert(typeof input === "string" && input.length > 0, "OCI loader scratch root path is required.");
  assert(typeof prefix === "string" && prefix.length > 0, "OCI loader scratch prefix path is required.");
  const absolute = resolve(input);
  const absolutePrefix = resolve(prefix);
  assert(normalizedPath(dirname(absolute)) === normalizedPath(dirname(absolutePrefix))
    && basename(absolute).startsWith(basename(absolutePrefix))
    && basename(absolute).length > basename(absolutePrefix).length, "OCI loader scratch root is outside the exact run-owned prefix.");
  return absolute;
}

function validateScratchPrefix(input) {
  assert(typeof input === "string" && input.length > 0, "OCI loader scratch prefix path is required.");
  const absolute = resolve(input);
  assert(basename(absolute).length > 0, "OCI loader scratch prefix name is required.");
  assertRegularNonLinkDirectory(dirname(absolute), "OCI loader scratch parent");
  assert(!absolute.includes(","), "OCI loader scratch prefix path must not contain a comma.");
  return absolute;
}

function validateScratchRoot(input, prefix) {
  const ownedRoot = scratchRootPathForPrefix(input, prefix);
  const absolute = assertRegularNonLinkDirectory(ownedRoot, "OCI loader scratch root");
  assert(normalizedPath(absolute) === normalizedPath(ownedRoot), "OCI loader scratch root must equal the exact run-owned path.");
  return absolute;
}

function defaultCreateScratch(prefix, registerCreated) {
  const exactPrefix = validateScratchPrefix(prefix);
  const scratchRoot = mkdtempSync(exactPrefix);
  try {
    registerCreated(scratchRoot);
    return scratchRoot;
  } catch (error) {
    try {
      const exactRoot = scratchRootPathForPrefix(scratchRoot, exactPrefix);
      rmSync(exactRoot, { recursive: true, force: true });
    } catch (cleanupError) {
      throw new Error(`${compactError(error)} OCI loader scratch registration rollback failed: ${compactError(cleanupError)}`, { cause: error });
    }
    throw error;
  }
}

function defaultValidateScratchArchive(input, scratchRoot, maximumBytes) {
  const exactRoot = assertRegularNonLinkDirectory(scratchRoot, "OCI loader scratch root");
  const expected = resolve(exactRoot, "candidate.docker.tar");
  assert(normalizedPath(resolve(input)) === normalizedPath(expected), "Scratch Docker archive must be inside the exact run-owned scratch root.");
  const checked = assertRegularNonLinkFile(input, "Scratch Docker archive");
  assert(normalizedPath(dirname(checked.path)) === normalizedPath(exactRoot), "Scratch Docker archive must be directly inside the exact run-owned scratch root.");
  assert(checked.links === 1, "Scratch Docker archive must not be a hard link.");
  assert(Number.isSafeInteger(checked.size) && checked.size > 0, "Scratch Docker archive must be a non-empty file with a safe size.");
  assert(Number.isSafeInteger(maximumBytes) && maximumBytes > 0 && checked.size <= maximumBytes, "Scratch Docker archive size must not exceed 8 GiB.");
  return checked.path;
}

function defaultRemoveScratch(scratchRoot, prefix) {
  const exactRoot = validateScratchRoot(scratchRoot, prefix);
  rmSync(exactRoot, { recursive: true, force: true });
}

function parseJsonFile(input, label) {
  const checked = assertRegularNonLinkFile(input, label);
  assert(checked.size > 0 && checked.size <= MAX_JSON_BYTES, `${label} must be non-empty and no larger than one MiB.`);
  try {
    return { path: checked.path, value: JSON.parse(readFileSync(checked.path, "utf8")) };
  } catch {
    throw new Error(`${label} must contain valid JSON.`);
  }
}

function tarText(header, start, length) {
  return header.subarray(start, start + length).toString("utf8").replace(/\0.*$/s, "").trim();
}

function tarSize(header) {
  const raw = tarText(header, 124, 12);
  assert(/^[0-7]+$/.test(raw), "OCI archive contains an invalid tar entry size.");
  const size = Number.parseInt(raw, 8);
  assert(Number.isSafeInteger(size) && size >= 0, "OCI archive contains an unsafe tar entry size.");
  return size;
}

function readExactly(file, buffer, position) {
  let offset = 0;
  while (offset < buffer.length) {
    const count = readSync(file, buffer, offset, buffer.length - offset, position + offset);
    if (count === 0) break;
    offset += count;
  }
  return offset;
}

function readArchiveEntries(file, actualSize) {
  const entriesByName = new Map();
  let position = 0;
  let entries = 0;
  while (position + 512 <= actualSize) {
    assert(entries++ < 100_000, "OCI archive contains too many tar entries.");
    const header = Buffer.alloc(512);
    assert(readExactly(file, header, position) === 512, "OCI archive ended in a partial tar header.");
    if (header.every((byte) => byte === 0)) break;
    const name = [tarText(header, 345, 155), tarText(header, 0, 100)].filter(Boolean).join("/").replace(/^\.\//, "");
    const size = tarSize(header);
    const body = position + 512;
    const next = body + Math.ceil(size / 512) * 512;
    assert(next <= actualSize, `OCI archive entry ${name || "<unnamed>"} exceeds the archive boundary.`);
    const matches = entriesByName.get(name) ?? [];
    matches.push({ body, size, type: tarText(header, 156, 1) });
    entriesByName.set(name, matches);
    position = next;
  }
  return entriesByName;
}

function readArchiveEntry(file, entriesByName, name, label) {
  const matches = entriesByName.get(name) ?? [];
  assert(matches.length === 1, `${label} must appear exactly once in the OCI archive.`);
  const entry = matches[0];
  assert(entry.type === "" || entry.type === "0", `${label} must be a regular non-link tar entry.`);
  assert(entry.size > 0 && entry.size <= MAX_JSON_BYTES, `${label} must be non-empty and no larger than one MiB.`);
  const payload = Buffer.alloc(entry.size);
  assert(readExactly(file, payload, entry.body) === entry.size, `${label} ended unexpectedly.`);
  return payload;
}

function parseJsonBytes(bytes, label) {
  try { return JSON.parse(bytes.toString("utf8")); }
  catch { throw new Error(`${label} must contain valid JSON.`); }
}

function sha256Digest(bytes) {
  return `sha256:${createHash("sha256").update(bytes).digest("hex")}`;
}

function validateBlobDescriptor(value, mediaType, label) {
  assert(value?.mediaType === mediaType, `${label} media type is invalid.`);
  assert(DIGEST.test(value?.digest ?? ""), `${label} digest must be sha256.`);
  assert(Number.isSafeInteger(value?.size) && value.size > 0 && value.size <= MAX_JSON_BYTES, `${label} size is unsafe.`);
  return value;
}

function jsonObjectPropertyNamesAtPath(bytes, targetPath, label) {
  const text = bytes.toString("utf8");
  const propertyNames = [];
  let position = 0;
  const skipWhitespace = () => {
    while (/\s/.test(text[position] ?? "")) position += 1;
  };
  const readString = () => {
    skipWhitespace();
    const start = position;
    assert(text[position] === "\"", `${label} JSON token structure is invalid.`);
    position += 1;
    while (position < text.length) {
      if (text[position] === "\\") position += 2;
      else if (text[position] === "\"") {
        position += 1;
        return JSON.parse(text.slice(start, position));
      } else position += 1;
    }
    throw new Error(`${label} JSON string ended unexpectedly.`);
  };
  const isTarget = (path) => path.length === targetPath.length
    && path.every((segment, index) => segment === targetPath[index]);
  function visitValue(path, depth) {
    assert(depth <= 256, `${label} JSON nesting is unsafe.`);
    skipWhitespace();
    if (text[position] === "{") visitObject(path, depth);
    else if (text[position] === "[") visitArray(path, depth);
    else if (text[position] === "\"") readString();
    else {
      while (position < text.length && !/[\s,\]}]/.test(text[position])) position += 1;
    }
  }
  function visitObject(path, depth) {
    position += 1;
    skipWhitespace();
    if (text[position] === "}") {
      position += 1;
      return;
    }
    while (position < text.length) {
      const property = readString();
      if (isTarget(path)) propertyNames.push(property);
      skipWhitespace();
      assert(text[position] === ":", `${label} JSON token structure is invalid.`);
      position += 1;
      visitValue([...path, property], depth + 1);
      skipWhitespace();
      if (text[position] === "}") {
        position += 1;
        return;
      }
      assert(text[position] === ",", `${label} JSON token structure is invalid.`);
      position += 1;
    }
  }
  function visitArray(path, depth) {
    position += 1;
    skipWhitespace();
    if (text[position] === "]") {
      position += 1;
      return;
    }
    while (position < text.length) {
      visitValue([...path, null], depth + 1);
      skipWhitespace();
      if (text[position] === "]") {
        position += 1;
        return;
      }
      assert(text[position] === ",", `${label} JSON token structure is invalid.`);
      position += 1;
    }
  }
  visitValue([], 0);
  return propertyNames;
}

function assertRequiredJsonPropertiesOnce(bytes, properties, label) {
  if (properties.length === 0) return;
  const propertyNames = jsonObjectPropertyNamesAtPath(bytes, ["config", "Labels"], label);
  for (const property of properties) {
    const count = propertyNames.filter((candidate) => candidate === property).length;
    const propertyLabel = property.slice("org.opencontainers.image.".length).replace("licenses", "license");
    assert(count === 1, count > 1
      ? `${label} contains a duplicate ${propertyLabel} label; it must appear exactly once.`
      : `${label} ${propertyLabel} label must appear exactly once.`);
  }
}

function readVerifiedJsonBlob(file, entriesByName, descriptor, label, requiredProperties = []) {
  const name = `blobs/sha256/${descriptor.digest.slice("sha256:".length)}`;
  const bytes = readArchiveEntry(file, entriesByName, name, `${label} blob`);
  assert(sha256Digest(bytes) === descriptor.digest, `${label} digest must equal the selected blob bytes.`);
  assert(bytes.length === descriptor.size, `${label} size must equal the selected blob byte length.`);
  const value = parseJsonBytes(bytes, label);
  assertRequiredJsonPropertiesOnce(bytes, requiredProperties, label);
  return value;
}

function validateLayerDescriptor(value, index) {
  const label = `OCI manifest layer ${index + 1}`;
  assert(LAYER_MEDIA_TYPES.has(value?.mediaType), `${label} media type is invalid.`);
  assert(DIGEST.test(value?.digest ?? ""), `${label} digest must be sha256.`);
  assert(Number.isSafeInteger(value?.size) && value.size > 0, `${label} size must be a positive safe integer.`);
}

function expectedImageLabels(kind, version, sourceSha) {
  return Object.freeze({
    "org.opencontainers.image.title": `Cmsify ${kind === "api" ? "API" : "Admin"}`,
    "org.opencontainers.image.source": "https://github.com/Syntax-Circus/cmsify",
    "org.opencontainers.image.revision": sourceSha,
    "org.opencontainers.image.version": version,
    "org.opencontainers.image.licenses": "AGPL-3.0-or-later",
  });
}

function readOciArchiveEvidence(input, { canonicalRef, certified, kind, sourceSha, version }) {
  const checked = assertRegularNonLinkFile(input, "OCI archive");
  assert(checked.size >= 1536, "OCI archive is too small to contain an OCI layout.");
  const file = openSync(checked.path, "r");
  try {
    const actualSize = fstatSync(file).size;
    assert(actualSize === checked.size, "OCI archive changed while it was being opened.");
    const entriesByName = readArchiveEntries(file, actualSize);
    const layout = parseJsonBytes(readArchiveEntry(file, entriesByName, "oci-layout", "OCI archive oci-layout"), "OCI archive oci-layout");
    const index = parseJsonBytes(readArchiveEntry(file, entriesByName, "index.json", "OCI archive index.json"), "OCI archive index.json");
    assert(layout?.imageLayoutVersion === "1.0.0", "OCI archive must contain OCI layout version 1.0.0.");

    const selected = (index?.manifests ?? []).filter((descriptor) => descriptor?.annotations?.["org.opencontainers.image.ref.name"] === version
      && descriptor?.annotations?.["io.containerd.image.name"] === canonicalRef);
    assert(selected.length === 1, `OCI archive index must select exactly one descriptor by tag and canonical containerd name for ${kind}.`);
    const descriptor = validateBlobDescriptor(selected[0], OCI_MANIFEST, "OCI manifest descriptor");
    assert(descriptor.digest === certified.digest, `OCI archive selected descriptor digest must equal the release-manifest ${kind} digest.`);
    assert(descriptor.mediaType === certified.mediaType && descriptor.size === certified.size, `OCI archive selected descriptor metadata must equal the release-manifest ${kind} descriptor.`);
    assert(descriptor.platform?.os === "linux" && descriptor.platform?.architecture === "amd64", `OCI archive selected descriptor must be linux/amd64.`);

    const manifest = readVerifiedJsonBlob(file, entriesByName, descriptor, "OCI manifest");
    assert(manifest?.schemaVersion === 2, "OCI manifest schema version must be 2.");
    assert(manifest?.mediaType === OCI_MANIFEST, "OCI manifest media type is invalid.");
    const configDescriptor = validateBlobDescriptor(manifest.config, OCI_CONFIG, "OCI config");
    assert(Array.isArray(manifest.layers) && manifest.layers.length > 0, "OCI manifest layer list must be a non-empty array.");
    manifest.layers.forEach(validateLayerDescriptor);

    const expectedLabels = expectedImageLabels(kind, version, sourceSha);
    const config = readVerifiedJsonBlob(file, entriesByName, configDescriptor, "OCI config", Object.keys(expectedLabels));
    assert(config?.os === "linux" && config?.architecture === "amd64", "OCI config platform must be linux/amd64.");
    assert(config?.rootfs?.type === "layers", "OCI config rootfs type must be layers.");
    const diffIds = config?.rootfs?.diff_ids;
    assert(Array.isArray(diffIds) && diffIds.length > 0, "OCI config DiffID list must be a non-empty array.");
    assert(diffIds.length === manifest.layers.length, "OCI config DiffID count must equal the manifest layer count.");
    diffIds.forEach((value, index) => assert(DIGEST.test(value ?? ""), `OCI config DiffID ${index + 1} must be sha256.`));

    const labels = config?.config?.Labels;
    for (const [key, expected] of Object.entries(expectedLabels)) {
      const label = key.slice("org.opencontainers.image.".length).replace("licenses", "license");
      assert(labels?.[key] === expected, `OCI config ${label} label must equal ${expected}.`);
    }
    assert(fstatSync(file).size === actualSize, "OCI archive changed while its evidence was being read.");
    return {
      path: checked.path,
      configDigest: configDescriptor.digest,
      diffIds: [...diffIds],
      expectedLabels,
    };
  } finally {
    closeSync(file);
  }
}

function isStrictSemVer(value) {
  const match = SEMVER.exec(value);
  if (!match) return false;
  return match[1] === undefined || match[1].split(".").every((identifier) => !/^\d+$/.test(identifier) || identifier === "0" || !identifier.startsWith("0"));
}

function validateInputs(options) {
  assert(options && typeof options === "object", "OCI loader options are required.");
  assert(["api", "admin"].includes(options.kind), "OCI loader kind must be api or admin.");
  assert(typeof options.version === "string", "OCI loader version must be a string.");
  assert(!options.version.includes("+"), "OCI loader version must not contain SemVer build metadata because it is used as a Docker tag.");
  assert(isStrictSemVer(options.version), "OCI loader version must be strict SemVer 2.0 without leading-zero numeric identifiers.");
  assert(Buffer.byteLength(options.version, "utf8") <= 128 && DOCKER_TAG.test(options.version), "OCI loader version must be a valid Docker tag no longer than 128 bytes.");
  const manifest = parseJsonFile(options.manifest, "Release manifest");
  assert(manifest.value?.version === options.version, `Release manifest version must equal ${options.version}.`);
  const sourceSha = manifest.value?.sourceSha;
  assert(/^[0-9a-f]{40}$/.test(sourceSha ?? ""), "Release manifest source SHA must be an exact lowercase 40-character commit SHA.");
  const certified = manifest.value?.oci?.[options.kind];
  const repository = `docker.io/syntaxcircus/cmsify-${options.kind}`;
  const canonicalRef = `${repository}:${options.version}`;
  assert(CANONICAL_REF.test(canonicalRef), `OCI loader canonical ref ${canonicalRef} is unsafe.`);
  assert(certified && typeof certified === "object", `Release manifest must bind OCI kind ${options.kind}.`);
  assert(certified.repository === repository, `Release manifest ${options.kind} repository must be ${repository}.`);
  assert(certified.ref === canonicalRef && certified.imageName === canonicalRef && certified.tag === options.version, `Release manifest ${options.kind} must bind the safe canonical ref ${canonicalRef}.`);
  assert(DIGEST.test(certified.digest ?? ""), `Release manifest ${options.kind} digest must be an exact sha256 digest.`);
  assert(certified.mediaType === OCI_MANIFEST, `Release manifest ${options.kind} must bind an OCI image manifest.`);
  assert(Number.isSafeInteger(certified.size) && certified.size > 0, `Release manifest ${options.kind} descriptor size must be a positive safe integer.`);
  assert(certified.size <= MAX_JSON_BYTES, `Release manifest ${options.kind} descriptor size is unsafe.`);
  assert(certified.platform?.os === "linux" && certified.platform?.architecture === "amd64", `Release manifest ${options.kind} must bind linux/amd64.`);
  const archive = readOciArchiveEvidence(options.archive, {
    canonicalRef,
    certified,
    kind: options.kind,
    sourceSha,
    version: options.version,
  });
  return {
    archivePath: archive.path,
    canonicalRef,
    certified,
    configDigest: archive.configDigest,
    diffIds: archive.diffIds,
    expectedLabels: archive.expectedLabels,
  };
}

function parseDockerJson(result, phase) {
  try { return JSON.parse(String(result.stdout).trim()); }
  catch { throw new Error(`Docker returned invalid JSON during ${phase}.`); }
}

function safeRunId(value) {
  const runId = value ?? `cmsify-oci-loader-${randomBytes(12).toString("hex")}`;
  assert(/^cmsify-oci-loader-[a-z0-9]{6,40}$/.test(runId), "OCI loader run ID is unsafe.");
  return runId;
}

function runResourceName(runId, suffix) {
  const name = `${runId}-${suffix}`;
  assert(Buffer.byteLength(name, "utf8") <= 128 && /^[a-z0-9][a-z0-9_.-]*$/.test(name), `OCI loader ${suffix} name is unsafe.`);
  return name;
}

function compactError(error) {
  return String(error?.message ?? error ?? "unknown failure").replace(/[\r\n]+/g, " ").slice(0, 512);
}

function isExactDockerTargetEquivalent(actual, expected, kind) {
  return actual === expected
    || (kind === "image" && expected.startsWith("docker.io/") && actual === expected.slice("docker.io/".length));
}

function isMissingDockerTarget(error, kind, target) {
  if (error?.exitCode !== 1) return false;
  const output = `${error?.stderr ?? ""}\n${error?.stdout ?? ""}\n${error?.message ?? ""}`;
  const diagnostic = new RegExp(`No such ${kind}:\\s*(\\S+)(?:\\s|$)`, "gi");
  return [...output.matchAll(diagnostic)].some((match) => isExactDockerTargetEquivalent(match[1], target, kind));
}

export async function loadOciCandidate(options, dependencies = {}) {
  const input = validateInputs(options);
  const run = dependencies.run ?? runProcess;
  const runId = safeRunId(dependencies.runId);
  const skopeoName = runResourceName(runId, "skopeo");
  const scratchPrefix = resolve(tmpdir(), "cmsify-oci-loader-");
  const createScratch = dependencies.createScratch ?? defaultCreateScratch;
  const validateScratchArchive = dependencies.validateScratchArchive ?? defaultValidateScratchArchive;
  const removeScratch = dependencies.removeScratch ?? ((scratchRoot) => defaultRemoveScratch(scratchRoot, scratchPrefix));
  let skopeoCleanupIntent = false;
  let scratchRoot;
  let scratchCleanupIntent = false;
  let canonicalCleanupIntent = false;
  let loadedVerified = false;
  let primaryError;
  const cleanupErrors = [];
  const execute = (args, phase) => run("docker", args, { timeoutMs: PROCESS_TIMEOUT_MS, phase });
  const assertAbsent = async (args, phase, kind, target, label) => {
    try { await execute(args, phase); }
    catch (error) {
      if (isMissingDockerTarget(error, kind, target)) return;
      throw error;
    }
    throw new Error(`${label} already exists; refusing to mutate it.`);
  };
  const cleanup = async (args, phase, kind, target) => {
    try { await execute(args, phase); }
    catch (error) {
      if (isMissingDockerTarget(error, kind, target)) return;
      cleanupErrors.push(`${phase}: ${compactError(error)}`);
    }
  };
  const registerScratch = (created) => {
    const exactRoot = validateScratchRoot(created, scratchPrefix);
    if (scratchCleanupIntent) {
      assert(normalizedPath(exactRoot) === normalizedPath(scratchRoot), "OCI loader scratch creation returned conflicting roots.");
      return;
    }
    scratchRoot = exactRoot;
    scratchCleanupIntent = true;
  };

  try {
    await assertAbsent(["image", "inspect", input.canonicalRef], "oci-loader-canonical-preflight", "image", input.canonicalRef, "Canonical candidate ref");
    await assertAbsent(["container", "inspect", skopeoName], "oci-loader-skopeo-preflight", "container", skopeoName, "Run-owned Skopeo container");
    await execute(["image", "pull", "--platform", "linux/amd64", SKOPEO_IMAGE], "oci-loader-skopeo-image-pull");

    const createdScratch = createScratch(scratchPrefix, registerScratch);
    if (!scratchCleanupIntent) registerScratch(createdScratch);
    else assert(normalizedPath(resolve(createdScratch)) === normalizedPath(scratchRoot), "OCI loader scratch creation returned a different root than it registered.");
    const dockerArchive = resolve(scratchRoot, "candidate.docker.tar");

    skopeoCleanupIntent = true;
    await execute([
      "run", "--rm", "--pull=never", "--platform", "linux/amd64", "--name", skopeoName,
      "--network", "none", "--label", LABEL_OWNER, "--label", `${LABEL_RUN}=${runId}`,
      "--mount", `type=bind,source=${input.archivePath},target=/candidate.oci.tar,readonly`,
      "--mount", `type=bind,source=${scratchRoot},target=/scratch`,
      SKOPEO_IMAGE, "copy",
      `oci-archive:/candidate.oci.tar:${options.version}`,
      `docker-archive:/scratch/candidate.docker.tar:${input.canonicalRef}`,
    ], "oci-loader-skopeo-copy");

    validateScratchArchive(dockerArchive, scratchRoot, MAX_DOCKER_ARCHIVE_BYTES);
    canonicalCleanupIntent = true;
    await execute(["image", "load", "--input", dockerArchive, "--platform", "linux/amd64"], "oci-loader-docker-load");
    const loaded = parseDockerJson(
      await execute(["image", "inspect", "--format", "{{json .}}", input.canonicalRef], "oci-loader-canonical-inspect"),
      "canonical candidate inspection",
    );
    assert(loaded.Id === input.configDigest, `Loaded image ID must equal OCI config digest ${input.configDigest}.`);
    assert(loaded.Os === "linux" && loaded.Architecture === "amd64", "Loaded candidate must be linux/amd64.");
    assert(Array.isArray(loaded.RootFS?.Layers)
      && loaded.RootFS.Layers.length === input.diffIds.length
      && loaded.RootFS.Layers.every((value, index) => value === input.diffIds[index]), "Loaded RootFS DiffIDs must equal OCI config order.");
    assert(Array.isArray(loaded.RepoTags)
      && loaded.RepoTags.some((value) => isExactDockerTargetEquivalent(value, input.canonicalRef, "image")), "Loaded candidate must have the exact canonical tag.");
    for (const [key, expected] of Object.entries(input.expectedLabels)) {
      const label = key.slice("org.opencontainers.image.".length).replace("licenses", "license");
      assert(loaded.Config?.Labels?.[key] === expected, `Loaded candidate ${label} label must equal ${expected}.`);
    }
    loadedVerified = true;
    return {
      ref: input.canonicalRef,
      digest: input.certified.digest,
      imageId: input.configDigest,
      diffIds: [...input.diffIds],
    };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    if (skopeoCleanupIntent) await cleanup(["container", "rm", "--force", skopeoName], "oci-loader-cleanup-skopeo", "container", skopeoName);
    if (scratchCleanupIntent) {
      try { removeScratch(scratchRoot); }
      catch (error) { cleanupErrors.push(`oci-loader-cleanup-scratch: ${compactError(error)}`); }
    }
    if (canonicalCleanupIntent && (!loadedVerified || cleanupErrors.length > 0)) {
      await cleanup(["image", "rm", input.canonicalRef], "oci-loader-cleanup-canonical", "image", input.canonicalRef);
    }
    if (cleanupErrors.length > 0) {
      const message = `${primaryError ? `${compactError(primaryError)} ` : ""}OCI loader cleanup failed: ${cleanupErrors.join("; ").slice(0, 1024)}`;
      if (!primaryError) throw new Error(message);
      throw new Error(message, { cause: primaryError });
    }
  }
}

export function parseCliArguments(argv) {
  if (!Array.isArray(argv)) throw new Error("OCI loader arguments must be an array.");
  if (argv.length === 1 && argv[0] === "--describe") return { command: "describe" };
  if (argv[0] !== "load") throw new Error("Usage: load-oci-candidate.mjs load --archive <oci.tar> --manifest <release-manifest.json> --kind <api|admin> --version <semver>.");
  const result = { command: "load" };
  for (let index = 1; index < argv.length; index += 2) {
    const flag = argv[index];
    const property = FLAGS[flag];
    assert(property, `Unknown OCI loader argument ${String(flag)}.`);
    assert(!Object.hasOwn(result, property), `Duplicate OCI loader argument ${flag}.`);
    const value = argv[index + 1];
    assert(typeof value === "string" && value.length > 0 && !value.startsWith("--"), `OCI loader argument ${flag} requires a value.`);
    result[property] = value;
  }
  const missing = Object.entries(FLAGS).filter(([, property]) => !Object.hasOwn(result, property)).map(([flag]) => flag);
  assert(missing.length === 0, `Required OCI loader arguments are missing: ${missing.join(", ")}.`);
  return result;
}

export async function main(argv = process.argv.slice(2), dependencies = {}) {
  const parsed = parseCliArguments(argv);
  if (parsed.command === "describe") {
    process.stdout.write(`${JSON.stringify(LOADER_CONTRACT)}\n`);
    return LOADER_CONTRACT;
  }
  const result = await loadOciCandidate(parsed, dependencies);
  process.stdout.write(`${JSON.stringify({ status: "loaded", ...result })}\n`);
  return result;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    process.stderr.write(`${compactError(error)}\n`);
    process.exitCode = 1;
  });
}
