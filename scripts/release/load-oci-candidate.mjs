#!/usr/bin/env node
import { randomBytes } from "node:crypto";
import {
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  readSync,
  realpathSync,
} from "node:fs";
import { dirname, parse, relative, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { pathToFileURL } from "node:url";

import { runProcess } from "../../eng/upgrade-tests/process.mjs";

export const REGISTRY_IMAGE = "docker.io/library/registry:2.8.3@sha256:46faa9a1ae6813194b53921a370f2f4f8c5e1aae228a89bceafef5847a6a3278";
export const SKOPEO_IMAGE = "quay.io/skopeo/stable:v1.22.2@sha256:f7cfa282082cbfc25b754905225985584d1fbc410fef99e1b498c9b64087b755";
export const LOADER_CONTRACT = Object.freeze({
  schema: "cmsify.oci-loader.v1",
  registryImage: REGISTRY_IMAGE,
  skopeoImage: SKOPEO_IMAGE,
});

const FLAGS = Object.freeze({
  "--archive": "archive",
  "--manifest": "manifest",
  "--kind": "kind",
  "--version": "version",
});
const SEMVER = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json";
const MAX_JSON_BYTES = 1024 * 1024;
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
  return { path: absolute, size: entry.size };
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

function readOciArchiveDocuments(input) {
  const checked = assertRegularNonLinkFile(input, "OCI archive");
  assert(checked.size >= 1536, "OCI archive is too small to contain an OCI layout.");
  const file = openSync(checked.path, "r");
  const documents = new Map();
  try {
    const actualSize = fstatSync(file).size;
    assert(actualSize === checked.size, "OCI archive changed while it was being opened.");
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
      if (["index.json", "oci-layout"].includes(name)) {
        assert(!documents.has(name), `OCI archive must contain exactly one ${name}.`);
        assert(size > 0 && size <= MAX_JSON_BYTES, `OCI archive ${name} must be non-empty and no larger than one MiB.`);
        const payload = Buffer.alloc(size);
        assert(readExactly(file, payload, body) === size, `OCI archive ${name} ended unexpectedly.`);
        try { documents.set(name, JSON.parse(payload.toString("utf8"))); }
        catch { throw new Error(`OCI archive ${name} must contain valid JSON.`); }
      }
      position = next;
    }
  } finally {
    closeSync(file);
  }
  assert(documents.get("oci-layout")?.imageLayoutVersion === "1.0.0", "OCI archive must contain OCI layout version 1.0.0.");
  assert(documents.has("index.json"), "OCI archive must contain index.json.");
  return { path: checked.path, index: documents.get("index.json") };
}

function validateInputs(options) {
  assert(options && typeof options === "object", "OCI loader options are required.");
  assert(["api", "admin"].includes(options.kind), "OCI loader kind must be api or admin.");
  assert(typeof options.version === "string" && SEMVER.test(options.version), "OCI loader version must be exact SemVer.");
  const manifest = parseJsonFile(options.manifest, "Release manifest");
  const archive = readOciArchiveDocuments(options.archive);
  assert(manifest.value?.version === options.version, `Release manifest version must equal ${options.version}.`);
  const certified = manifest.value?.oci?.[options.kind];
  const repository = `docker.io/syntaxcircus/cmsify-${options.kind}`;
  const canonicalRef = `${repository}:${options.version}`;
  assert(certified && typeof certified === "object", `Release manifest must bind OCI kind ${options.kind}.`);
  assert(certified.repository === repository, `Release manifest ${options.kind} repository must be ${repository}.`);
  assert(certified.ref === canonicalRef && certified.imageName === canonicalRef && certified.tag === options.version, `Release manifest ${options.kind} must bind the safe canonical ref ${canonicalRef}.`);
  assert(DIGEST.test(certified.digest ?? ""), `Release manifest ${options.kind} digest must be an exact sha256 digest.`);
  assert(certified.mediaType === OCI_MANIFEST, `Release manifest ${options.kind} must bind an OCI image manifest.`);
  assert(certified.platform?.os === "linux" && certified.platform?.architecture === "amd64", `Release manifest ${options.kind} must bind linux/amd64.`);
  const selected = (archive.index?.manifests ?? []).filter((descriptor) => descriptor?.annotations?.["org.opencontainers.image.ref.name"] === options.version
    && descriptor?.annotations?.["io.containerd.image.name"] === canonicalRef);
  assert(selected.length === 1, `OCI archive index must select exactly one descriptor by tag and canonical containerd name for ${options.kind}.`);
  const descriptor = selected[0];
  assert(descriptor.digest === certified.digest, `OCI archive selected descriptor digest must equal the release-manifest ${options.kind} digest.`);
  assert(descriptor.mediaType === certified.mediaType && descriptor.size === certified.size, `OCI archive selected descriptor metadata must equal the release-manifest ${options.kind} descriptor.`);
  assert(descriptor.platform?.os === "linux" && descriptor.platform?.architecture === "amd64", `OCI archive selected descriptor must be linux/amd64.`);
  return { archivePath: archive.path, canonicalRef, certified };
}

function parseDockerJson(result, phase) {
  try { return JSON.parse(String(result.stdout).trim()); }
  catch { throw new Error(`Docker returned invalid JSON during ${phase}.`); }
}

function parseRegistryPort(result) {
  const match = /^127\.0\.0\.1:(\d{1,5})$/.exec(String(result.stdout).trim());
  const port = Number(match?.[1]);
  assert(Number.isInteger(port) && port >= 1 && port <= 65535, "Isolated registry must publish one loopback TCP port.");
  return port;
}

async function defaultWaitForRegistry(url) {
  let lastError;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(1000) });
      if (response.status === 200) return;
      lastError = new Error(`HTTP ${response.status}`);
    } catch (error) { lastError = error; }
    await delay(250);
  }
  throw new Error(`Isolated registry did not become ready: ${String(lastError?.message ?? "no response").replace(/[\r\n]+/g, " ").slice(0, 256)}`);
}

function safeRunId(value) {
  const runId = value ?? `cmsify-oci-loader-${randomBytes(12).toString("hex")}`;
  assert(/^cmsify-oci-loader-[a-z0-9]{6,40}$/.test(runId), "OCI loader run ID is unsafe.");
  return runId;
}

function compactError(error) {
  return String(error?.message ?? error ?? "unknown failure").replace(/[\r\n]+/g, " ").slice(0, 512);
}

export async function loadOciCandidate(options, dependencies = {}) {
  const input = validateInputs(options);
  const run = dependencies.run ?? runProcess;
  const waitForRegistry = dependencies.waitForRegistry ?? defaultWaitForRegistry;
  const runId = safeRunId(dependencies.runId);
  const networkName = `${runId}-network`;
  const registryName = `${runId}-registry`;
  let networkCreated = false;
  let registryCreated = false;
  let intermediateRef;
  let intermediatePulled = false;
  let canonicalCreated = false;
  let completed = false;
  let primaryError;
  const cleanupErrors = [];
  const execute = (args, phase) => run("docker", args, { timeoutMs: PROCESS_TIMEOUT_MS, phase });
  const cleanup = async (args, phase) => {
    try { await execute(args, phase); }
    catch (error) { cleanupErrors.push(`${phase}: ${compactError(error)}`); }
  };

  try {
    await execute(["image", "pull", "--platform", "linux/amd64", REGISTRY_IMAGE], "oci-loader-registry-image-pull");
    await execute(["image", "pull", "--platform", "linux/amd64", SKOPEO_IMAGE], "oci-loader-skopeo-image-pull");
    const network = await execute(["network", "create", "--label", LABEL_OWNER, "--label", `${LABEL_RUN}=${runId}`, networkName], "oci-loader-network-create");
    networkCreated = true;
    const returnedNetworkId = String(network.stdout).trim();
    assert(/^[a-f0-9]{12,64}$|^network-id$/.test(returnedNetworkId), "Docker did not return a safe isolated network ID.");
    const registry = await execute([
      "run", "--detach", "--pull=never", "--platform", "linux/amd64", "--name", registryName,
      "--network", networkName, "--network-alias", "registry", "--publish", "127.0.0.1::5000",
      "--label", LABEL_OWNER, "--label", `${LABEL_RUN}=${runId}`, REGISTRY_IMAGE,
    ], "oci-loader-registry-start");
    registryCreated = true;
    const returnedRegistryId = String(registry.stdout).trim();
    assert(/^[a-f0-9]{12,64}$|^registry-container-id$/.test(returnedRegistryId), "Docker did not return a safe isolated registry container ID.");
    const published = await execute(["container", "port", registryName, "5000/tcp"], "oci-loader-registry-port");
    const port = parseRegistryPort(published);
    await waitForRegistry(`http://127.0.0.1:${port}/v2/`);
    const localRepository = `127.0.0.1:${port}/cmsify-${options.kind}`;
    intermediateRef = `${localRepository}:${runId}`;
    await execute([
      "run", "--rm", "--pull=never", "--platform", "linux/amd64", "--network", networkName,
      "--mount", `type=bind,source=${input.archivePath},target=/candidate.oci.tar,readonly`,
      SKOPEO_IMAGE, "copy", "--preserve-digests", "--dest-tls-verify=false",
      `oci-archive:/candidate.oci.tar:${options.version}`, `docker://registry:5000/cmsify-${options.kind}:${runId}`,
    ], "oci-loader-skopeo-copy");
    await execute(["image", "pull", "--platform", "linux/amd64", intermediateRef], "oci-loader-candidate-pull");
    intermediatePulled = true;
    const inspected = parseDockerJson(await execute(["image", "inspect", "--format", "{{json .}}", intermediateRef], "oci-loader-candidate-inspect"), "candidate inspection");
    const expectedRepoDigest = `${localRepository}@${input.certified.digest}`;
    assert(Array.isArray(inspected.RepoDigests) && inspected.RepoDigests.includes(expectedRepoDigest), `Loopback candidate RepoDigest must equal certified destination digest ${input.certified.digest}.`);
    assert(/^sha256:[0-9a-f]{64}$/.test(inspected.Id ?? ""), "Loopback candidate must have an immutable Docker image ID.");
    await execute(["image", "tag", inspected.Id, input.canonicalRef], "oci-loader-canonical-tag");
    canonicalCreated = true;
    const canonical = parseDockerJson(await execute(["image", "inspect", "--format", "{{json .}}", input.canonicalRef], "oci-loader-canonical-inspect"), "canonical candidate inspection");
    assert(canonical.Id === inspected.Id && Array.isArray(canonical.RepoTags) && canonical.RepoTags.includes(input.canonicalRef), "Canonical candidate tag must resolve to the exact digest-verified image ID.");
    completed = true;
    return { ref: input.canonicalRef, digest: input.certified.digest, imageId: inspected.Id };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    if (canonicalCreated && !completed) await cleanup(["image", "rm", input.canonicalRef], "oci-loader-cleanup-canonical");
    if (intermediatePulled) await cleanup(["image", "rm", intermediateRef], "oci-loader-cleanup-intermediate");
    if (registryCreated) await cleanup(["container", "rm", "--force", registryName], "oci-loader-cleanup-registry");
    if (networkCreated) await cleanup(["network", "rm", networkName], "oci-loader-cleanup-network");
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
