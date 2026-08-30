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

function processBoundary({ digest, kind = "api", failPhase } = {}) {
  const calls = [];
  const canonicalRef = `docker.io/syntaxcircus/cmsify-${kind}:${VERSION}`;
  const intermediateRef = `127.0.0.1:43123/cmsify-${kind}:cmsify-oci-loader-test123`;
  const imageId = `sha256:${"a".repeat(64)}`;
  const run = async (command, args, processOptions) => {
    const call = { command, args: [...args], phase: processOptions.phase };
    calls.push(call);
    if (processOptions.phase === failPhase) throw new Error(`injected ${failPhase} failure`);
    switch (processOptions.phase) {
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
  return { calls, run, canonicalRef, intermediateRef, imageId };
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

test("cleans every created run-owned resource at each failure boundary", async (context) => {
  const root = createValidCandidate();
  context.after(() => removeCandidate(root));
  const manifest = JSON.parse(readFileSync(candidatePath(root, "release-manifest.json"), "utf8"));
  const { loadOciCandidate } = await loaderModule();
  const cases = [
    ["oci-loader-registry-start", ["oci-loader-cleanup-network"]],
    ["oci-loader-registry-port", ["oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-skopeo-copy", ["oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-candidate-pull", ["oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-candidate-inspect", ["oci-loader-cleanup-intermediate", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-canonical-tag", ["oci-loader-cleanup-intermediate", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
    ["oci-loader-canonical-inspect", ["oci-loader-cleanup-canonical", "oci-loader-cleanup-intermediate", "oci-loader-cleanup-registry", "oci-loader-cleanup-network"]],
  ];
  for (const [failPhase, expectedCleanup] of cases) {
    const boundary = processBoundary({ digest: manifest.oci.api.digest, failPhase });
    await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: "cmsify-oci-loader-test123", waitForRegistry: async () => {} }), new RegExp(`injected ${failPhase}`));
    const phases = boundary.calls.map((call) => call.phase);
    for (const cleanup of expectedCleanup) assert.equal(phases.includes(cleanup), true, `${failPhase} omitted ${cleanup}`);
    const failureIndex = phases.indexOf(failPhase);
    assert.equal(expectedCleanup.every((cleanup) => phases.indexOf(cleanup) > failureIndex), true, `${failPhase} cleanup did not run after failure`);
  }
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
