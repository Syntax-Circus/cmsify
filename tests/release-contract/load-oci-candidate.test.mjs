import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import test from "node:test";

import {
  VERSION,
  candidatePath,
  createValidCandidate,
  mutateJsonFile,
  mutateOciLayout,
  removeCandidate,
} from "./release-candidate-fixture.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const loaderUrl = pathToFileURL(resolve(repositoryRoot, "scripts", "release", "load-oci-candidate.mjs")).href;
const registryImage = "docker.io/library/registry:2.8.3@sha256:46faa9a1ae6813194b53921a370f2f4f8c5e1aae228a89bceafef5847a6a3278";
const skopeoImage = "quay.io/skopeo/stable:v1.22.2@sha256:f7cfa282082cbfc25b754905225985584d1fbc410fef99e1b498c9b64087b755";

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
  mutateOciLayout(root, "api", ({ descriptor }) => {
    descriptor.annotations["org.opencontainers.image.ref.name"] = version;
    descriptor.annotations["io.containerd.image.name"] = ref;
  });
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

function processBoundary({ digest, kind = "api", version = VERSION, failPhase, existingCanonical = false, occupiedPreflight, cleanupTargetsAlreadyAbsent = false } = {}) {
  const calls = [];
  const runId = "cmsify-oci-loader-test123";
  const canonicalRef = `docker.io/syntaxcircus/cmsify-${kind}:${version}`;
  const networkName = `${runId}-network`;
  const registryName = `${runId}-registry`;
  const skopeoName = `${runId}-skopeo`;
  const intermediateRef = `127.0.0.1:43123/cmsify-${kind}:${runId}`;
  const imageId = `sha256:${"a".repeat(64)}`;
  const run = async (command, args, processOptions) => {
    const call = { command, args: [...args], phase: processOptions.phase };
    calls.push(call);
    if (processOptions.phase === failPhase) throw dockerTimeout(failPhase);
    if (cleanupTargetsAlreadyAbsent && processOptions.phase.startsWith("oci-loader-cleanup-")) {
      if (processOptions.phase === "oci-loader-cleanup-network") throw dockerMissing(`network ${networkName} not found`);
      if (processOptions.phase === "oci-loader-cleanup-registry") throw dockerMissing(`No such container: ${registryName}`);
      if (processOptions.phase === "oci-loader-cleanup-skopeo") throw dockerMissing(`No such container: ${skopeoName}`);
      if (processOptions.phase === "oci-loader-cleanup-intermediate") throw dockerMissing(`No such image: ${intermediateRef}`);
      if (processOptions.phase === "oci-loader-cleanup-canonical") throw dockerMissing(`No such image: ${canonicalRef}`);
    }
    switch (processOptions.phase) {
      case "oci-loader-canonical-preflight":
        if (!existingCanonical && occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such image: ${canonicalRef}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Id: `sha256:${"b".repeat(64)}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-network-preflight":
        if (occupiedPreflight !== processOptions.phase) throw dockerMissing(`network ${networkName} not found`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Name: networkName })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-registry-preflight":
        if (occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such container: ${registryName}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Name: `/${registryName}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-skopeo-preflight":
        if (occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such container: ${skopeoName}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Name: `/${skopeoName}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-intermediate-preflight":
        if (occupiedPreflight !== processOptions.phase) throw dockerMissing(`No such image: ${intermediateRef}`);
        return { exitCode: 0, stdout: `${JSON.stringify({ Id: `sha256:${"c".repeat(64)}` })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-network-create": return { exitCode: 0, stdout: "network-id\n", stderr: "", durationMs: 1 };
      case "oci-loader-registry-start": return { exitCode: 0, stdout: "registry-container-id\n", stderr: "", durationMs: 1 };
      case "oci-loader-registry-port": return { exitCode: 0, stdout: "127.0.0.1:43123\n", stderr: "", durationMs: 1 };
      case "oci-loader-candidate-inspect":
        return { exitCode: 0, stdout: `${JSON.stringify({ Id: imageId, RepoDigests: [`127.0.0.1:43123/cmsify-${kind}@${digest}`] })}\n`, stderr: "", durationMs: 1 };
      case "oci-loader-canonical-inspect":
        return { exitCode: 0, stdout: `${JSON.stringify({ Id: imageId, RepoTags: [canonicalRef] })}\n`, stderr: "", durationMs: 1 };
      default: return { exitCode: 0, stdout: "", stderr: "", durationMs: 1 };
    }
  };
  return { calls, run, canonicalRef, intermediateRef, imageId, networkName, registryName, skopeoName };
}

function commandText(call) {
  return [call.command, ...call.args].join(" ");
}

test("imports a real OCI-layout fixture without native docker load, rebuilding, a Docker socket mount, or an external candidate pull", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest });
    const { loadOciCandidate } = await loaderModule();

    const result = await loadOciCandidate(options(root), {
      run: boundary.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    });

    assert.deepEqual(result, { ref: boundary.canonicalRef, digest: manifest.oci.api.digest, imageId: boundary.imageId });
    const commands = boundary.calls.map(commandText);
    assert.equal(commands.some((command) => /\bdocker (?:image )?load\b/.test(command)), false);
    assert.equal(commands.some((command) => /\bdocker (?:build|buildx)\b/.test(command)), false);
    assert.equal(commands.some((command) => /docker(?:_engine|\.sock)|\/var\/run\/docker\.sock/i.test(command)), false);
    const pulls = boundary.calls.filter((call) => call.args[0] === "image" && call.args[1] === "pull").map((call) => call.args.at(-1));
    assert.deepEqual(pulls, [registryImage, skopeoImage, boundary.intermediateRef]);
    assert.equal(pulls.some((reference) => reference === boundary.canonicalRef), false);
    const copy = boundary.calls.find((call) => call.phase === "oci-loader-skopeo-copy");
    assert.ok(copy, "Skopeo copy process boundary was not exercised");
    assert.equal(copy.args.includes(skopeoImage), true);
    assert.equal(copy.args.includes("--preserve-digests"), true);
    assert.equal(copy.args.some((argument) => argument.includes("readonly")), true);
    assert.equal(copy.args.includes(`oci-archive:/candidate.oci.tar:${VERSION}`), true);
    assert.equal(copy.args[copy.args.indexOf("--name") + 1], boundary.skopeoName);
    assert.equal(copy.args.includes("--label"), true);
    assert.equal(copy.args.includes("io.syntaxcircus.cmsify.oci-loader=true"), true);
    assert.equal(copy.args.includes("io.syntaxcircus.cmsify.oci-loader-run=cmsify-oci-loader-test123"), true);
    assert.deepEqual(boundary.calls.filter((call) => call.phase.endsWith("-preflight")).map((call) => call.phase), [
      "oci-loader-canonical-preflight",
      "oci-loader-network-preflight",
      "oci-loader-registry-preflight",
      "oci-loader-skopeo-preflight",
      "oci-loader-intermediate-preflight",
    ]);
  } finally { removeCandidate(root); }
});

test("creates an internal helper network with no external egress", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest });
    const { loadOciCandidate } = await loaderModule();

    await loadOciCandidate(options(root), {
      run: boundary.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    });

    const networkCreate = boundary.calls.find((call) => call.phase === "oci-loader-network-create");
    assert.equal(networkCreate?.args.includes("--internal"), true);
  } finally { removeCandidate(root); }
});

test("rejects a release-manifest digest that does not select the archive descriptor before Docker", async () => {
  const root = createValidCandidate();
  try {
    mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.digest = `sha256:${"f".repeat(64)}`; });
    const boundary = processBoundary({ digest: `sha256:${"f".repeat(64)}` });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /descriptor.*digest|digest.*descriptor/i);
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
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
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
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
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
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /select exactly one descriptor|tag/i);
    assert.equal(boundary.calls.length, 0);
  } finally { removeCandidate(root); }
});

test("rejects unsafe canonical refs before Docker", async () => {
  const root = createValidCandidate();
  try {
    mutateJsonFile(root, "release-manifest.json", (manifest) => { manifest.oci.api.ref = `docker.io/syntaxcircus/cmsify-api:${VERSION};docker pull attacker.invalid/image`; });
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /canonical.*ref|unsafe.*ref/i);
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
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    }), /canonical.*already exists|ref.*collision/i);

    assert.deepEqual(boundary.calls.map((call) => call.phase), ["oci-loader-canonical-preflight"]);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-canonical-tag"), false);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-canonical"), false);
  } finally { removeCandidate(root); }
});

test("rejects collisions on exact run-owned resource names before their create commands", async (context) => {
  const root = createValidCandidate();
  context.after(() => removeCandidate(root));
  const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
  const { loadOciCandidate } = await loaderModule();
  const cases = [
    ["oci-loader-network-preflight", "oci-loader-network-create"],
    ["oci-loader-registry-preflight", "oci-loader-registry-start"],
    ["oci-loader-skopeo-preflight", "oci-loader-skopeo-copy"],
    ["oci-loader-intermediate-preflight", "oci-loader-candidate-pull"],
  ];

  for (const [occupiedPreflight, forbiddenMutation] of cases) {
    const boundary = processBoundary({ digest: manifest.oci.api.digest, occupiedPreflight });
    await assert.rejects(loadOciCandidate(options(root), {
      run: boundary.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    }), /already exists|collision/i);
    assert.equal(boundary.calls.some((call) => call.phase === forbiddenMutation), false, `${occupiedPreflight} allowed ${forbiddenMutation}`);
  }
});

test("rejects linked archive and manifest ancestors before Docker", async () => {
  const root = createValidCandidate();
  const linkParent = mkdtempSync(resolve(tmpdir(), "cmsify-oci-loader-link-"));
  const linkedRoot = resolve(linkParent, "candidate-link");
  try {
    symlinkSync(root, linkedRoot, process.platform === "win32" ? "junction" : "dir");
    const boundary = processBoundary({ digest: JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8")).oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate({ ...options(root), archive: candidatePath(linkedRoot, "oci/cmsify-api.oci.tar") }, { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /link|reparse/i);
    await assert.rejects(loadOciCandidate({ ...options(root), manifest: candidatePath(linkedRoot, "release-manifest.json") }, { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /link|reparse/i);
    assert.equal(boundary.calls.length, 0);
  } finally {
    rmSync(linkParent, { recursive: true, force: true });
    removeCandidate(root);
  }
});

test("rejects a loopback pull whose RepoDigest differs from the certified descriptor", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: `sha256:${"e".repeat(64)}` });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /RepoDigest.*certified|destination.*digest/i);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-canonical-tag"), false);
    assert.equal(boundary.calls.some((call) => call.phase === "oci-loader-cleanup-intermediate"), true);
  } finally { removeCandidate(root); }
});

test("cleans exact run-owned resources when create commands time out after side effects and when later phases fail", async (context) => {
  const root = createValidCandidate();
  context.after(() => removeCandidate(root));
  const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
  const { loadOciCandidate } = await loaderModule();
  const cases = [
    ["oci-loader-network-create", ["oci-loader-cleanup-network"]],
    ["oci-loader-registry-start", ["oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-registry-port", ["oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-skopeo-copy", ["oci-loader-cleanup-skopeo", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-candidate-pull", ["oci-loader-cleanup-intermediate", "oci-loader-cleanup-skopeo", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-candidate-inspect", ["oci-loader-cleanup-intermediate", "oci-loader-cleanup-skopeo", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-canonical-tag", ["oci-loader-cleanup-canonical", "oci-loader-cleanup-intermediate", "oci-loader-cleanup-skopeo", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-canonical-inspect", ["oci-loader-cleanup-canonical", "oci-loader-cleanup-intermediate", "oci-loader-cleanup-skopeo", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
  ];
  for (const [failPhase, expectedCleanup] of cases) {
    const boundary = processBoundary({ digest: manifest.oci.api.digest, failPhase });
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), new RegExp(`injected ${failPhase}`));
    const phases = boundary.calls.map((call) => call.phase);
    for (const cleanup of expectedCleanup) assert.equal(phases.includes(cleanup), true, `${failPhase} omitted ${cleanup}`);
    const failureIndex = phases.indexOf(failPhase);
    assert.equal(expectedCleanup.every((cleanup) => phases.indexOf(cleanup) > failureIndex), true, `${failPhase} cleanup did not run after failure`);
    const targets = boundary.calls.filter((call) => expectedCleanup.includes(call.phase)).map((call) => call.args.at(-1));
    const expectedTargets = expectedCleanup.map((cleanup) => ({
      "oci-loader-cleanup-canonical": boundary.canonicalRef,
      "oci-loader-cleanup-intermediate": boundary.intermediateRef,
      "oci-loader-cleanup-skopeo": boundary.skopeoName,
      "oci-loader-cleanup-registry": boundary.registryName,
      "oci-loader-cleanup-network": boundary.networkName,
    })[cleanup]);
    assert.deepEqual(targets, expectedTargets, `${failPhase} cleanup selected an unexpected target`);
  }
});

test("treats already-absent exact cleanup targets as clean while real cleanup errors remain blocking", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const { loadOciCandidate } = await loaderModule();
    const absent = processBoundary({ digest: manifest.oci.api.digest, cleanupTargetsAlreadyAbsent: true });
    const result = await loadOciCandidate(options(root), {
      run: absent.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    });
    assert.deepEqual(result, { ref: absent.canonicalRef, digest: manifest.oci.api.digest, imageId: absent.imageId });
    assert.equal(absent.calls.some((call) => call.phase === "oci-loader-cleanup-skopeo"), true);

    const blocked = processBoundary({ digest: manifest.oci.api.digest, failPhase: "oci-loader-cleanup-network" });
    await assert.rejects(loadOciCandidate(options(root), {
      run: blocked.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => {},
    }), /cleanup failed.*cleanup-network.*injected/i);
  } finally { removeCandidate(root); }
});

test("cleans the registry and network when readiness fails outside the process runner", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest });
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), {
      run: boundary.run,
      runId: "cmsify-oci-loader-test123",
      waitForRegistry: async () => { throw new Error("injected registry readiness failure"); },
    }), /registry readiness failure/i);
    const phases = boundary.calls.map((call) => call.phase);
    assert.equal(phases.includes("oci-loader-cleanup-registry"), true);
    assert.equal(phases.includes("oci-loader-cleanup-network"), true);
  } finally { removeCandidate(root); }
});

test("uses only known run-owned names for cleanup when Docker returns malformed resource IDs", async () => {
  const root = createValidCandidate();
  try {
    const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
    const boundary = processBoundary({ digest: manifest.oci.api.digest });
    const run = async (command, args, processOptions) => {
      const result = await boundary.run(command, args, processOptions);
      if (processOptions.phase === "oci-loader-registry-start") return { ...result, stdout: "../untrusted-target\n" };
      return result;
    };
    const { loadOciCandidate } = await loaderModule();
    await assert.rejects(loadOciCandidate(options(root), { run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), /safe isolated registry container ID/i);
    const registryCleanup = boundary.calls.find((call) => call.phase === "oci-loader-cleanup-registry");
    const networkCleanup = boundary.calls.find((call) => call.phase === "oci-loader-cleanup-network");
    assert.deepEqual(registryCleanup?.args, ["container", "rm", "--force", "cmsify-oci-loader-test123-registry"]);
    assert.deepEqual(networkCleanup?.args, ["network", "rm", "cmsify-oci-loader-test123-network"]);
  } finally { removeCandidate(root); }
});
