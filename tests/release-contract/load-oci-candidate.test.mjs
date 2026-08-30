import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, truncateSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import test from "node:test";

import {
  SOURCE_SHA,
  VERSION,
  candidatePath,
  createValidCandidate,
  mutateJsonFile,
  mutateOciLayout,
  readOciFixtureEvidence,
  removeCandidate,
} from "./release-candidate-fixture.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const loaderUrl = pathToFileURL(resolve(repositoryRoot, "scripts", "release", "load-oci-candidate.mjs")).href;
const skopeoImage = "quay.io/skopeo/stable:v1.22.2@sha256:f7cfa282082cbfc25b754905225985584d1fbc410fef99e1b498c9b64087b755";
const RUN_ID = "cmsify-oci-loader-test123";

async function loaderModule() {
  return import(`${loaderUrl}?test=${Date.now()}-${Math.random()}`);
}

function options(root, kind = "api") {
  return {
    archive: candidatePath(root, `oci/cmsify-${kind}.oci.tar`),
    manifest: candidatePath(root, "release-manifest.json"),
    kind,
    version: VERSION,
  };
}

function bindApiVersion(root, version) {
  const repository = "docker.io/syntaxcircus/cmsify-api";
  const ref = `${repository}:${version}`;
  mutateJsonFile(root, "release-manifest.json", (manifest) => {
    manifest.version = version;
    manifest.oci.api.repository = repository;
    manifest.oci.api.ref = ref;
    manifest.oci.api.imageName = ref;
    manifest.oci.api.tag = version;
  });
  mutateOciLayout(root, "api", ({ descriptor, config }) => {
    descriptor.annotations["org.opencontainers.image.ref.name"] = version;
    descriptor.annotations["io.containerd.image.name"] = ref;
    config.config.Labels["org.opencontainers.image.version"] = version;
  }, { resealConfig: true, resealManifest: true });
}

function dockerMissing(message) {
  const error = new Error(message);
  error.exitCode = 1;
  error.stdout = "";
  error.stderr = `Error response from daemon: ${message}\n`;
  return error;
}

function dockerTimeout(phase) {
  const error = new Error(`injected ${phase} timeout after side effect`);
  error.exitCode = null;
  error.phase = `${phase}: timeout`;
  return error;
}

function processBoundary({
  evidence = {
    configDigest: `sha256:${"a".repeat(64)}`,
    diffIds: [`sha256:${"b".repeat(64)}`],
    labels: {
      "org.opencontainers.image.title": "Cmsify API",
      "org.opencontainers.image.source": "https://github.com/Syntax-Circus/cmsify",
      "org.opencontainers.image.revision": SOURCE_SHA,
      "org.opencontainers.image.version": VERSION,
      "org.opencontainers.image.licenses": "AGPL-3.0-or-later",
    },
  },
  kind = "api",
  version = VERSION,
  failPhase,
  existingCanonical = false,
  occupiedPreflight,
  cleanupTargetsAlreadyAbsent = false,
  dockerHubShortCanonicalDiagnostic = false,
  dockerHubShortLoadedTag = false,
  mutateLoadedImage,
  failCreateScratchAfterCreate = false,
  failScratchCleanup = false,
} = {}) {
  const calls = [];
  const runId = RUN_ID;
  const canonicalRef = `docker.io/syntaxcircus/cmsify-${kind}:${version}`;
  const skopeoName = `${runId}-skopeo`;
  const loadedImage = {
    Id: evidence.configDigest,
    Os: "linux",
    Architecture: "amd64",
    RootFS: { Layers: [...evidence.diffIds] },
    RepoTags: [dockerHubShortLoadedTag ? canonicalRef.slice("docker.io/".length) : canonicalRef],
    Config: { Labels: { ...evidence.labels } },
  };
  mutateLoadedImage?.(loadedImage);
  const canonicalMissingTarget = dockerHubShortCanonicalDiagnostic ? canonicalRef.replace(/^docker\.io\//, "") : canonicalRef;
  const scratchValidations = [];
  const scratchRemovals = [];
  let scratchRoot;
  let dockerArchive;
  const createScratch = (prefix, registerCreated) => {
    scratchRoot = mkdtempSync(prefix);
    registerCreated?.(scratchRoot);
    if (failCreateScratchAfterCreate) throw new Error("injected create-then-throw scratch failure");
    return scratchRoot;
  };
  const validateScratchArchive = (archive, root, maximumBytes) => {
    dockerArchive = archive;
    scratchValidations.push({ archive, root, maximumBytes });
  };
  const removeScratch = (root) => {
    scratchRemovals.push(root);
    if (failScratchCleanup) throw new Error("injected scratch cleanup failure");
    rmSync(root, { recursive: true, force: true });
  };
  const run = async (command, args, processOptions) => {
    const call = { command, args: [...args], phase: processOptions.phase };
    calls.push(call);
    if (processOptions.phase === failPhase) throw dockerTimeout(failPhase);
    if (cleanupTargetsAlreadyAbsent && processOptions.phase.startsWith("oci-loader-cleanup-")) {
      if (processOptions.phase === "oci-loader-cleanup-skopeo") throw dockerMissing(`No such container: ${skopeoName}`);
      if (processOptions.phase === "oci-loader-cleanup-canonical") throw dockerMissing(`No such image: ${canonicalMissingTarget}`);
    }
    switch (processOptions.phase) {
      case "oci-loader-canonical-preflight":
        if (!existingCanonical && occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such image: ${canonicalMissingTarget}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Id: `sha256:${"b".repeat(64)}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-skopeo-preflight":
        if (occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such container: ${skopeoName}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Name: `/${skopeoName}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-canonical-inspect":
        return { exitCode: 0, stdout: `${JSON.stringify(loadedImage)}\n`, stderr: "", durationMs: 1 };
      default: return { exitCode: 0, stdout: "", stderr: "", durationMs: 1 };
    }
  };
  return {
    calls,
    run,
    runId,
    canonicalRef,
    configDigest: evidence.configDigest,
    diffIds: [...evidence.diffIds],
    labels: { ...evidence.labels },
    skopeoName,
    createScratch,
    validateScratchArchive,
    removeScratch,
    scratchValidations,
    scratchRemovals,
    get scratchRoot() { return scratchRoot; },
    get dockerArchive() { return dockerArchive; },
  };
}

function commandText(call) {
  return [call.command, ...call.args].join(" ");
}

function loaderDependencies(boundary, overrides = {}) {
  return {
    run: boundary.run,
    runId: RUN_ID,
    createScratch: boundary.createScratch,
    validateScratchArchive: boundary.validateScratchArchive,
    removeScratch: boundary.removeScratch,
    ...overrides,
  };
}

function evidenceTest(name, mutate, diagnostic, mutationOptions) {
  test(`rejects mismatched ${name} before Docker`, async () => {
    const root = createValidCandidate();
    try {
      mutateOciLayout(root, "api", mutate, mutationOptions);
      const releaseManifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
      const boundary = processBoundary({ digest: releaseManifest.oci.api.digest });
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), {
        run: boundary.run,
        runId: RUN_ID,
      }), diagnostic);
      assert.equal(boundary.calls.length, 0, `${name} reached the Docker process boundary`);
    } finally { removeCandidate(root); }
  });
}

for (const [name, mutate, diagnostic, mutationOptions] of [
  ["manifest bytes", ({ manifest }) => { manifest.layers[0].size += 1; }, /manifest.*digest|manifest.*size/i],
  ["config bytes", ({ config }) => { config.os = "windows"; }, /config.*digest|config.*platform/i],
  ["manifest schema version", ({ manifest }) => { manifest.schemaVersion = 1; }, /manifest.*schema.*2/i, { resealManifest: true }],
  ["manifest media type", ({ manifest }) => { manifest.mediaType = "application\/octet-stream"; }, /manifest.*media type/i, { resealManifest: true }],
  ["config media type", ({ manifest }) => { manifest.config.mediaType = "application\/octet-stream"; }, /config.*media type/i, { resealManifest: true }],
  ["empty layer list", ({ manifest }) => { manifest.layers = []; }, /layer.*non-empty|manifest.*layer/i, { resealManifest: true }],
  ["layer media type", ({ manifest }) => { manifest.layers[0].mediaType = "application\/octet-stream"; }, /layer.*media type/i, { resealManifest: true }],
  ["layer digest", ({ manifest }) => { manifest.layers[0].digest = "sha256:not-a-digest"; }, /layer.*digest/i, { resealManifest: true }],
  ["layer size", ({ manifest }) => { manifest.layers[0].size = 0; }, /layer.*size/i, { resealManifest: true }],
  ["rootfs type", ({ config }) => { config.rootfs.type = "unknown"; }, /rootfs.*layers/i, { resealConfig: true, resealManifest: true }],
  ["unsafe DiffID", ({ config }) => { config.rootfs.diff_ids[0] = "sha256:not-a-digest"; }, /DiffID/i, { resealConfig: true, resealManifest: true }],
  ["DiffID layer count", ({ config }) => { config.rootfs.diff_ids.pop(); }, /DiffID.*layer|layer.*DiffID/i, { resealConfig: true, resealManifest: true }],
]) evidenceTest(name, mutate, diagnostic, mutationOptions);

for (const [name, mutate, diagnostic, mutationOptions] of [
  ["manifest descriptor size", ({ descriptor }) => { descriptor.size += 1; }, /manifest.*size/i, { syncReleaseDescriptor: true }],
  ["manifest descriptor digest", ({ descriptor, manifestPath, writeBlob }) => {
    const digest = `sha256:${"1".repeat(64)}`;
    writeBlob(digest, readFileSync(manifestPath));
    descriptor.digest = digest;
  }, /manifest.*digest/i, { syncReleaseDescriptor: true }],
  ["absent selected manifest blob", ({ manifestPath, omitBlob }) => { omitBlob(manifestPath); }, /manifest.*blob|manifest.*exactly one/i],
  ["duplicate selected manifest blob", ({ manifestPath, duplicateBlob }) => { duplicateBlob(manifestPath); }, /manifest.*blob|manifest.*exactly one|duplicate/i],
  ["config descriptor size", ({ manifest }) => { manifest.config.size += 1; }, /config.*size/i, { resealManifest: true }],
  ["config descriptor digest", ({ manifest, configPath, writeBlob }) => {
    const digest = `sha256:${"2".repeat(64)}`;
    writeBlob(digest, readFileSync(configPath));
    manifest.config.digest = digest;
  }, /config.*digest/i, { resealManifest: true }],
  ["absent selected config blob", ({ configPath, omitBlob }) => { omitBlob(configPath); }, /config.*blob|config.*exactly one/i],
  ["duplicate selected config blob", ({ configPath, duplicateBlob }) => { duplicateBlob(configPath); }, /config.*blob|config.*exactly one|duplicate/i],
  ["config OS", ({ config }) => { config.os = "windows"; }, /config.*linux\/amd64|config.*platform/i, { resealConfig: true, resealManifest: true }],
  ["config architecture", ({ config }) => { config.architecture = "arm64"; }, /config.*linux\/amd64|config.*platform/i, { resealConfig: true, resealManifest: true }],
  ["non-array DiffID list", ({ config }) => { config.rootfs.diff_ids = "sha256:not-an-array"; }, /DiffID.*array/i, { resealConfig: true, resealManifest: true }],
  ["empty DiffID list", ({ config }) => { config.rootfs.diff_ids = []; }, /DiffID.*non-empty|DiffID.*array/i, { resealConfig: true, resealManifest: true }],
  ["malformed DiffID list", ({ config }) => { config.rootfs.diff_ids[1] = "sha256:not-a-digest"; }, /DiffID/i, { resealConfig: true, resealManifest: true }],
]) evidenceTest(name, mutate, diagnostic, mutationOptions);

for (const [label, key, value, diagnostic] of [
  ["title", "org.opencontainers.image.title", "Cmsify Admin", /title label/i],
  ["source", "org.opencontainers.image.source", "https:\/\/attacker.invalid\/cmsify", /source label/i],
  ["revision", "org.opencontainers.image.revision", "f".repeat(40), /revision label/i],
  ["version", "org.opencontainers.image.version", "9.9.9", /version label/i],
  ["license", "org.opencontainers.image.licenses", "MIT", /license label/i],
]) {
  evidenceTest(`required OCI label ${label}`, ({ config }) => {
    config.config.Labels[key] = value;
  }, diagnostic, { resealConfig: true, resealManifest: true });
}

evidenceTest("duplicate required OCI label", ({ config, setConfigBytes }) => {
  const key = "org.opencontainers.image.version";
  const encoded = JSON.stringify(config);
  const original = `${JSON.stringify(key)}:${JSON.stringify(VERSION)}`;
  assert.equal(encoded.includes(original), true);
  setConfigBytes(`${encoded.replace(original, `${JSON.stringify(key)}:"9.9.9",${original}`)}\n`);
}, /duplicate.*version label|version label.*exactly once/i, { resealConfig: true, resealManifest: true });

evidenceTest("escape-equivalent duplicate required OCI label", ({ config, setConfigBytes }) => {
  const key = "org.opencontainers.image.version";
  const encoded = JSON.stringify(config);
  const original = `${JSON.stringify(key)}:${JSON.stringify(VERSION)}`;
  assert.equal(encoded.includes(original), true);
  setConfigBytes(`${encoded.replace(original, `"\\u006frg.opencontainers.image.version":"9.9.9",${original}`)}\n`);
}, /duplicate.*version label|version label.*exactly once/i, { resealConfig: true, resealManifest: true });

test("allows a required-label-named property outside config Labels", async () => {
  const root = createValidCandidate();
  try {
    mutateOciLayout(root, "api", ({ config, setConfigBytes }) => {
      const encoded = JSON.stringify(config);
      const key = JSON.stringify("org.opencontainers.image.version");
      setConfigBytes(`${encoded.slice(0, -1)},${key}:"not-a-label"}\n`);
    }, { resealConfig: true, resealManifest: true });
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
    const { loadOciCandidate } = await loaderModule();

    const result = await loadOciCandidate(options(root), loaderDependencies(boundary));

    assert.equal(result.imageId, boundary.configDigest);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-canonical-inspect"), true);
  } finally { removeCandidate(root); }
});

evidenceTest("missing required OCI label", ({ config }) => {
  delete config.config.Labels["org.opencontainers.image.version"];
}, /version label.*exactly once/i, { resealConfig: true, resealManifest: true });

evidenceTest("malformed config JSON", ({ setConfigBytes }) => {
  setConfigBytes("{\n");
}, /config.*valid JSON/i, { resealConfig: true, resealManifest: true });

test("loads through an offline Docker archive with Skopeo network none", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
    const { loadOciCandidate } = await loaderModule();

    const result = await loadOciCandidate(options(root), loaderDependencies(boundary));

    assert.deepEqual(result, {
      ref: boundary.canonicalRef,
      digest: manifest.oci.api.digest,
      imageId: boundary.configDigest,
      diffIds: boundary.diffIds,
    });
    const commands = boundary.calls.map(commandText);
    assert.equal(commands.some((command) => /\bdocker (?:build|buildx)\b/.test(command)), false);
    assert.equal(commands.some((command) => /docker(?:_engine|\.sock)|\/var\/run\/docker\.sock/i.test(command)), false);
    assert.equal(commands.some((command) => /\bdocker network\b|registry:|\bdocker:\/\//i.test(command)), false);
    const pulls = boundary.calls.filter((call) => call.args[0] === "image" && call.args[1] === "pull").map((call) => call.args.at(-1));
    assert.deepEqual(pulls, [skopeoImage]);

    const copy = boundary.calls.find((call) => call.phase === "oci-loader-skopeo-copy");
    assert.ok(copy, "Skopeo copy process boundary was not exercised");
    assert.deepEqual(copy.args, [
      "run", "--rm", "--pull=never", "--platform", "linux/amd64", "--name", boundary.skopeoName,
      "--network", "none",
      "--label", "io.syntaxcircus.cmsify.oci-loader=true",
      "--label", `io.syntaxcircus.cmsify.oci-loader-run=${RUN_ID}`,
      "--mount", `type=bind,source=${options(root).archive},target=/candidate.oci.tar,readonly`,
      "--mount", `type=bind,source=${boundary.scratchRoot},target=/scratch`,
      skopeoImage,
      "copy",
      `oci-archive:/candidate.oci.tar:${VERSION}`,
      `docker-archive:/scratch/candidate.docker.tar:${boundary.canonicalRef}`,
    ]);

    const load = boundary.calls.find((call) => call.phase === "oci-loader-docker-load");
    assert.deepEqual(load?.args, ["image", "load", "--input", boundary.dockerArchive, "--platform", "linux/amd64"]);
    assert.notEqual(boundary.dockerArchive, options(root).archive);
    assert.deepEqual(boundary.calls.find((call) => call.phase === "oci-loader-canonical-inspect")?.args, [
      "image", "inspect", "--format", "{{json .}}", boundary.canonicalRef,
    ]);
    assert.deepEqual(boundary.scratchValidations, [{
      archive: boundary.dockerArchive,
      root: boundary.scratchRoot,
      maximumBytes: 8 * 1024 * 1024 * 1024,
    }]);
    assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), false);
    assert.deepEqual(boundary.calls.find((call) => call.phase === "oci-loader-cleanup-skopeo")?.args, ["container", "rm", "--force", boundary.skopeoName]);
  } finally { removeCandidate(root); }
});

test("offline Docker archive transport never creates networks or Registry and never pulls a candidate", async () => {
  const root = createValidCandidate();
  try {
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
    const { loadOciCandidate } = await loaderModule();

    await loadOciCandidate(options(root), loaderDependencies(boundary));

    const commands = boundary.calls.map(commandText);
    assert.equal(commands.some((command) => /\bdocker network (?:create|connect|rm)\b/i.test(command)), false);
    assert.equal(commands.some((command) => /registry|127\.0\.0\.1|localhost/i.test(command)), false);
    assert.equal(commands.some((command) => /\bdocker image pull\b.*cmsify-(?:api|admin)/i.test(command)), false);
    assert.equal(commands.some((command) => /\bdocker image load\b.*candidate\.oci\.tar/i.test(command)), false);
    assert.equal(commands.some((command) => /docker:\/\//i.test(command)), false);
  } finally { removeCandidate(root); }
});

for (const [name, mutate, diagnostic] of [
  ["ID", (image) => { image.Id = `sha256:${"f".repeat(64)}`; }, /image ID.*OCI config digest/i],
  ["OS", (image) => { image.Os = "windows"; }, /linux\/amd64/i],
  ["architecture", (image) => { image.Architecture = "arm64"; }, /linux\/amd64/i],
  ["canonical tag", (image) => { image.RepoTags = ["docker.io/syntaxcircus/cmsify-api:other"]; }, /exact canonical tag/i],
]) {
  test(`rejects mismatched loaded image identity ${name}`, async () => {
    const root = createValidCandidate();
    try {
      const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api"), mutateLoadedImage: mutate });
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), diagnostic);
      assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
      assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot]);
    } finally { removeCandidate(root); }
  });
}

test("accepts the exact Docker Hub-short RepoTag while returning the fully qualified canonical ref", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({
      evidence: readOciFixtureEvidence(root, "api"),
      dockerHubShortLoadedTag: true,
    });
    const { loadOciCandidate } = await loaderModule();

    const result = await loadOciCandidate(options(root), loaderDependencies(boundary));

    assert.deepEqual(result, {
      ref: "docker.io/syntaxcircus/cmsify-api:1.2.3",
      digest: manifest.oci.api.digest,
      imageId: boundary.configDigest,
      diffIds: boundary.diffIds,
    });
    assert.deepEqual(boundary.calls.find((call) => call.phase === "oci-loader-canonical-inspect")?.args, [
      "image", "inspect", "--format", "{{json .}}", "docker.io/syntaxcircus/cmsify-api:1.2.3",
    ]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), false);
  } finally { removeCandidate(root); }
});

for (const [name, repoTag] of [
  ["attacker prefix", "attacker.invalid/syntaxcircus/cmsify-api:1.2.3"],
  ["attacker suffix", "syntaxcircus/cmsify-api:1.2.3-attacker"],
  ["other registry", "ghcr.io/syntaxcircus/cmsify-api:1.2.3"],
  ["other repository", "syntaxcircus/cmsify-admin:1.2.3"],
  ["other tag", "syntaxcircus/cmsify-api:9.9.9"],
  ["digest reference", `syntaxcircus/cmsify-api@sha256:${"f".repeat(64)}`],
  ["basename only", "cmsify-api:1.2.3"],
  ["arbitrary basename only", "candidate:1.2.3"],
]) {
  test(`rejects non-equivalent loaded RepoTag ${name}`, async () => {
    const root = createValidCandidate();
    try {
      const boundary = processBoundary({
        evidence: readOciFixtureEvidence(root, "api"),
        mutateLoadedImage(image) { image.RepoTags = [repoTag]; },
      });
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), /exact canonical tag/i);
      assert.deepEqual(boundary.calls.find((call) => call.phase === "oci-loader-canonical-inspect")?.args, [
        "image", "inspect", "--format", "{{json .}}", "docker.io/syntaxcircus/cmsify-api:1.2.3",
      ]);
      assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
    } finally { removeCandidate(root); }
  });
}

for (const [label, key] of [
  ["title", "org.opencontainers.image.title"],
  ["source", "org.opencontainers.image.source"],
  ["revision", "org.opencontainers.image.revision"],
  ["version", "org.opencontainers.image.version"],
  ["license", "org.opencontainers.image.licenses"],
]) {
  test(`rejects mismatched loaded image identity ${label} label`, async () => {
    const root = createValidCandidate();
    try {
      const boundary = processBoundary({
        evidence: readOciFixtureEvidence(root, "api"),
        mutateLoadedImage(image) { image.Config.Labels[key] = "wrong"; },
      });
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), new RegExp(`${label} label`, "i"));
      assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
    } finally { removeCandidate(root); }
  });
}

for (const index of [0, 1]) {
  test(`rejects mismatched loaded image identity RootFS DiffID ${index + 1}`, async () => {
    const root = createValidCandidate();
    try {
      const boundary = processBoundary({
        evidence: readOciFixtureEvidence(root, "api"),
        mutateLoadedImage(image) { image.RootFS.Layers[index] = `sha256:${String(index + 8).repeat(64)}`; },
      });
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), /RootFS DiffIDs.*OCI config order/i);
      assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
    } finally { removeCandidate(root); }
  });
}

test("rejects a release-manifest digest that does not select the archive descriptor before Docker", async () => {
  const root = createValidCandidate();
  try {
    mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.digest = `sha256:${"f".repeat(64)}`; });
    const boundary = processBoundary({ digest: `sha256:${"f".repeat(64)}` });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: RUN_ID }), /descriptor.*digest|digest.*descriptor/i);
    assert.equal(boundary.calls.length, 0);
  } finally { removeCandidate(root); }
});

test("rejects non-positive, fractional, non-numeric, and unsafe certified descriptor sizes before Docker", async (context) => {
  const { loadOciCandidate } = await loaderModule();
  const invalidSizes = [0, -1, 1.5, "123", 9007199254740992];
  for (const size of invalidSizes) {
    const root = createValidCandidate();
    context.after(() => removeCandidate(root));
    mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.size = size; });
    mutateOciLayout(root, "api", ({ descriptor }) => { descriptor.size = size; });
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });

    await assert.rejects(loadOciCandidate(options(root), {
      run: boundary.run,
      runId: RUN_ID,
    }), /descriptor size.*positive safe integer/i);
    assert.equal(boundary.calls.length, 0, `invalid descriptor size ${JSON.stringify(size)} reached Docker`);
  }
});

test("rejects non-strict SemVer, build metadata, and overlong Docker tags before Docker", async (context) => {
  const { loadOciCandidate } = await loaderModule();
  const invalidVersions = [
    "01.2.3",
    "1.02.3",
    "1.2.03",
    "1.2.3-01",
    "1.2.3-alpha.01",
    "1.2.3+build.7",
    `1.2.3-${"a".repeat(123)}`,
  ];
  for (const version of invalidVersions) {
    const root = createValidCandidate();
    context.after(() => removeCandidate(root));
    bindApiVersion(root, version);
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest, version });

    await assert.rejects(loadOciCandidate({ ...options(root), version }, {
      run: boundary.run,
      runId: RUN_ID,
    }), /(?:exact|strict) SemVer|build metadata|Docker tag|canonical ref/i);
    assert.equal(boundary.calls.length, 0, `invalid version ${version} reached Docker`);
  }
});

test("rejects an OCI descriptor with the wrong selected tag before Docker", async () => {
  const root = createValidCandidate({
    afterRender({ root: candidate }) {
      mutateOciLayout(candidate, "api", ({ descriptor }) => { descriptor.annotations["org.opencontainers.image.ref.name"] = "wrong"; });
    },
  });
  try {
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: RUN_ID }), /select exactly one descriptor|tag/i);
    assert.equal(boundary.calls.length, 0);
  } finally { removeCandidate(root); }
});

test("rejects unsafe canonical refs before Docker", async () => {
  const root = createValidCandidate();
  try {
    mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.ref = `docker.io/syntaxcircus/cmsify-api:${VERSION};docker pull attacker.invalid/image`; });
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: RUN_ID }), /canonical.*ref|unsafe.*ref/i);
    assert.equal(boundary.calls.length, 0);
  } finally { removeCandidate(root); }
});

test("rejects a pre-existing canonical tag before mutation and never removes or retags it", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest, existingCanonical: true });
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate(options(root), {
      run: boundary.run,
      runId: RUN_ID,
    }), /canonical.*already exists|ref.*collision/i);

    assert.deepEqual(boundary.calls.map((call) => call.phase), ["oci-loader-canonical-preflight"]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-docker-load"), false);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), false);
  } finally { removeCandidate(root); }
});

test("accepts only the exact Docker Hub-short spelling of a missing canonical image", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const evidence = readOciFixtureEvidence(root, "api");
    const { loadOciCandidate } = await loaderModule();
    const normalized = processBoundary({
      evidence,
      dockerHubShortCanonicalDiagnostic: true,
    });

    const result = await loadOciCandidate(options(root), loaderDependencies(normalized));
    assert.deepEqual(result, {
      ref: normalized.canonicalRef,
      digest: manifest.oci.api.digest,
      imageId: normalized.configDigest,
      diffIds: normalized.diffIds,
    });
    assert.equal(normalized.calls.some((call) => call.phase === "oci-loader-docker-load"), true);

    const shortCanonicalRef = normalized.canonicalRef.replace(/^docker\.io\//, "");
    for (const target of [
      `attacker.invalid/${shortCanonicalRef}`,
      `${shortCanonicalRef}-attacker`,
    ]) {
      const rejected = processBoundary({ evidence });
      const phases = [];
      const run = async (command, args, processOptions) => {
        phases.push(processOptions.phase);
        if (processOptions.phase === "oci-loader-canonical-preflight") throw dockerMissing(`No such image: ${target}`);
        return rejected.run(command, args, processOptions);
      };
      await assert.rejects(loadOciCandidate(options(root), {
        run,
        runId: RUN_ID,
      }), /No such image/i);
      assert.deepEqual(phases, ["oci-loader-canonical-preflight"], `accepted a non-exact missing-image target ${target}`);
    }

    const nonExitOne = processBoundary({ evidence });
    const nonExitOnePhases = [];
    await assert.rejects(loadOciCandidate(options(root), {
      run: async (command, args, processOptions) => {
        nonExitOnePhases.push(processOptions.phase);
        if (processOptions.phase === "oci-loader-canonical-preflight") {
          const error = dockerMissing(`No such image: ${shortCanonicalRef}`);
          error.exitCode = 2;
          throw error;
        }
        return nonExitOne.run(command, args, processOptions);
      },
      runId: RUN_ID,
    }), /No such image/i);
    assert.deepEqual(nonExitOnePhases, ["oci-loader-canonical-preflight"]);

    const cleanup = processBoundary({
      evidence,
      failPhase: "oci-loader-canonical-inspect",
      cleanupTargetsAlreadyAbsent: true,
      dockerHubShortCanonicalDiagnostic: true,
    });
    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(cleanup)), (error) => {
      assert.match(error.message, /injected oci-loader-canonical-inspect timeout/i);
      assert.doesNotMatch(error.message, /OCI loader cleanup failed/i);
      return true;
    });
    assert.equal(cleanup.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
  } finally { removeCandidate(root); }
});

test("rejects a case-different missing canonical image target at preflight", async () => {
  const root = createValidCandidate();
  try {
    const version = "1.0.0-RC";
    bindApiVersion(root, version);
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api", version), version });
    const diagnosticTarget = "syntaxcircus/cmsify-api:1.0.0-rc";
    const phases = [];
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate({ ...options(root), version }, {
      run: async (command, args, processOptions) => {
        phases.push(processOptions.phase);
        if (processOptions.phase === "oci-loader-canonical-preflight") throw dockerMissing(`No such image: ${diagnosticTarget}`);
        return boundary.run(command, args, processOptions);
      },
      runId: RUN_ID,
    }), /No such image/i);
    assert.deepEqual(phases, ["oci-loader-canonical-preflight"]);
  } finally { removeCandidate(root); }
});

test("does not suppress cleanup failure for a case-different canonical image target", async () => {
  const root = createValidCandidate();
  try {
    const version = "1.0.0-RC";
    bindApiVersion(root, version);
    const boundary = processBoundary({
      evidence: readOciFixtureEvidence(root, "api", version),
      version,
      failPhase: "oci-loader-canonical-inspect",
      cleanupTargetsAlreadyAbsent: true,
    });
    const diagnosticTarget = "syntaxcircus/cmsify-api:1.0.0-rc";
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate({ ...options(root), version }, loaderDependencies(boundary, {
      run: async (command, args, processOptions) => {
        if (processOptions.phase === "oci-loader-cleanup-canonical") throw dockerMissing(`No such image: ${diagnosticTarget}`);
        return boundary.run(command, args, processOptions);
      },
    })), /cleanup failed.*cleanup-canonical.*No such image/i);
  } finally { removeCandidate(root); }
});

test("rejects an exact run-owned Skopeo name collision before scratch creation", async (context) => {
  const root = createValidCandidate();
  context.after(() => removeCandidate(root));
  const { loadOciCandidate } = await loaderModule();
  const boundary = processBoundary({
    evidence: readOciFixtureEvidence(root, "api"),
    occupiedPreflight: "oci-loader-skopeo-preflight",
  });

  await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), /Skopeo.*already exists|collision/i);
  assert.deepEqual(boundary.calls.map((call) => call.phase), ["oci-loader-canonical-preflight", "oci-loader-skopeo-preflight"]);
  assert.equal(boundary.scratchRoot, undefined);
  assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-skopeo-copy"), false);
});

test("rejects linked archive and manifest ancestors before Docker", async () => {
  const root = createValidCandidate();
  const linkParent = mkdtempSync(resolve(tmpdir(), "cmsify-oci-loader-link-"));
  const linkedRoot = resolve(linkParent, "candidate-link");
  try {
    symlinkSync(root, linkedRoot, process.platform === "win32" ? "junction" : "dir");
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate({ ...options(root), archive: candidatePath(linkedRoot, "oci/cmsify-api.oci.tar") }, { run: boundary.run, runId: RUN_ID }), /link|reparse/i);
    await assert.rejects(loadOciCandidate({ ...options(root), manifest: candidatePath(linkedRoot, "release-manifest.json") }, { run: boundary.run, runId: RUN_ID }), /link|reparse/i);
    assert.equal(boundary.calls.length, 0);
  } finally {
    rmSync(linkParent, { recursive: true, force: true });
    removeCandidate(root);
  }
});

test("scratch cleanup covers Skopeo copy, Docker load, and loaded image identity failures", async (context) => {
  const root = createValidCandidate();
  context.after(() => removeCandidate(root));
  const evidence = readOciFixtureEvidence(root, "api");
  const { loadOciCandidate } = await loaderModule();
  for (const [failPhase, canonicalCleanupExpected] of [
    ["oci-loader-skopeo-copy", false],
    ["oci-loader-docker-load", true],
    ["oci-loader-canonical-inspect", true],
  ]) {
    const boundary = processBoundary({ evidence, failPhase });
    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), new RegExp(`injected ${failPhase}`));
    assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot], `${failPhase} did not remove its exact scratch root`);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-skopeo"), true, `${failPhase} omitted Skopeo cleanup`);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), canonicalCleanupExpected, `${failPhase} canonical cleanup intent was wrong`);
  }
});

test("create-then-throw scratch creation still removes only the registered exact root", async () => {
  const root = createValidCandidate();
  try {
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api"), failCreateScratchAfterCreate: true });
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), /create-then-throw scratch failure/i);
    assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-skopeo-copy"), false);
  } finally { removeCandidate(root); }
});

test("production-default scratch registration failure does not leak its exact created directory", async () => {
  const root = createValidCandidate();
  const tempRoot = mkdtempSync(resolve(tmpdir(), "cmsify-oci-loader-temp-parent-"));
  const commaParent = resolve(tempRoot, "parent,with-comma");
  const tempVariables = ["TMPDIR", "TMP", "TEMP"];
  const previous = new Map(tempVariables.map((name) => [name, process.env[name]]));
  mkdirSync(commaParent);
  try {
    for (const name of tempVariables) process.env[name] = commaParent;
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate(options(root), {
      run: boundary.run,
      runId: RUN_ID,
    }), /scratch.*comma|path.*comma/i);

    assert.deepEqual(readdirSync(commaParent), [], "default scratch creation leaked its exact mkdtemp directory");
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-skopeo-copy"), false);
  } finally {
    for (const [name, value] of previous) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    rmSync(tempRoot, { recursive: true, force: true });
    removeCandidate(root);
  }
});

test("scratch cleanup runs when archive validation rejects before Docker load", async () => {
  const root = createValidCandidate();
  try {
    const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary, {
      validateScratchArchive() { throw new Error("injected scratch archive validation failure"); },
    })), /scratch archive validation failure/i);
    assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-docker-load"), false);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), false);
  } finally { removeCandidate(root); }
});

test("scratch cleanup failure preserves the bounded primary diagnostic and removes the loaded tag", async () => {
  const root = createValidCandidate();
  try {
    const boundary = processBoundary({
      evidence: readOciFixtureEvidence(root, "api"),
      failPhase: "oci-loader-canonical-inspect",
      failScratchCleanup: true,
    });
    const { loadOciCandidate } = await loaderModule();

    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(boundary)), /canonical-inspect.*cleanup failed.*scratch/i);
    assert.deepEqual(boundary.scratchRemovals, [boundary.scratchRoot]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
    rmSync(boundary.scratchRoot, { recursive: true, force: true });
  } finally { removeCandidate(root); }
});

test("treats already-absent exact cleanup targets as clean while real cleanup errors remain blocking", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const evidence = readOciFixtureEvidence(root, "api");
    const { loadOciCandidate } = await loaderModule();
    const absent = processBoundary({ evidence, cleanupTargetsAlreadyAbsent: true });
    const result = await loadOciCandidate(options(root), loaderDependencies(absent));
    assert.deepEqual(result, {
      ref: absent.canonicalRef,
      digest: manifest.oci.api.digest,
      imageId: absent.configDigest,
      diffIds: absent.diffIds,
    });
    assert.equal(absent.calls.some((call) => call.phase === "oci-loader-cleanup-skopeo"), true);

    const blocked = processBoundary({ evidence, failPhase: "oci-loader-cleanup-skopeo" });
    await assert.rejects(loadOciCandidate(options(root), loaderDependencies(blocked)), /cleanup failed.*cleanup-skopeo.*injected/i);
    assert.equal(blocked.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), true);
  } finally { removeCandidate(root); }
});

for (const [name, createArtifact, diagnostic] of [
  ["zero-length file", (archive) => { writeFileSync(archive, ""); }, /non-empty|zero length/i],
  ["directory", (archive) => { mkdirSync(archive); }, /regular non-link file/i],
  ["reparse point", (archive) => {
    const target = mkdtempSync(resolve(tmpdir(), "cmsify-oci-loader-link-target-"));
    symlinkSync(target, archive, process.platform === "win32" ? "junction" : "dir");
    return () => rmSync(target, { recursive: true, force: true });
  }, /link|reparse/i],
  ["over-8-GiB file", (archive) => {
    writeFileSync(archive, "x");
    truncateSync(archive, 8 * 1024 * 1024 * 1024 + 1);
  }, /8 GiB|maximum|size/i],
]) {
  test(`rejects unsafe scratch archive ${name} and performs scratch cleanup`, async () => {
    const root = createValidCandidate();
    let scratchRoot;
    let disposeArtifact = () => {};
    try {
      const boundary = processBoundary({ evidence: readOciFixtureEvidence(root, "api") });
      const run = async (command, args, processOptions) => {
        if (processOptions.phase === "oci-loader-skopeo-copy") {
          const scratchMount = args.find((arg) => typeof arg === "string" && arg.endsWith(",target=/scratch"));
          if (scratchMount) {
            scratchRoot = scratchMount.slice("type=bind,source=".length, -",target=/scratch".length);
            disposeArtifact = createArtifact(resolve(scratchRoot, "candidate.docker.tar")) ?? disposeArtifact;
          }
        }
        return boundary.run(command, args, processOptions);
      };
      const { loadOciCandidate } = await loaderModule();

      await assert.rejects(loadOciCandidate(options(root), { run, runId: RUN_ID }), diagnostic);
      assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-docker-load"), false);
      assert.equal(existsSync(scratchRoot), false);
    } finally {
      disposeArtifact();
      if (scratchRoot) rmSync(scratchRoot, { recursive: true, force: true });
      removeCandidate(root);
    }
  });
}

test("describes the offline Docker archive contract without registry topology", async () => {
  const { LOADER_CONTRACT } = await loaderModule();
  assert.deepEqual(LOADER_CONTRACT, {
    schema: "cmsify.oci-loader.v1",
    skopeoImage,
    transport: "offline-docker-archive",
  });
});
