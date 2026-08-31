# Offline OCI Candidate Loader Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the non-portable local-registry OCI loader with an offline, networkless Skopeo conversion whose Docker-loaded runtime identity is cryptographically tied to the certified OCI config and ordered filesystem DiffIDs.

**Architecture:** The original OCI archive remains the only certified and promoted image artifact. The loader verifies the selected OCI manifest and config blobs, runs digest-pinned Skopeo with `--network none` to create a run-owned Docker archive, loads that scratch archive, and accepts the image only when Docker's image ID, platform, labels, and ordered `RootFS.Layers` equal the verified OCI config evidence.

**Tech Stack:** Node.js ESM, Node test runner, OCI Image Layout/Manifest JSON, Docker CLI, digest-pinned Skopeo `v1.22.2`, GitHub Actions YAML.

**Spec:** `docs/superpowers/specs/2026-08-30-offline-oci-loader-transport-design.md`

## Global Constraints

- The original OCI archive, its selected descriptor, and `SHA256SUMS` remain the certified artifact identity; the Docker archive is disposable transport scratch and never a release subject.
- Keep the CLI stable: `load --archive <oci.tar> --manifest <release-manifest.json> --kind <api|admin> --version <semver>`.
- Keep exact helper identity `quay.io/skopeo/stable:v1.22.2@sha256:f7cfa282082cbfc25b754905225985584d1fbc410fef99e1b498c9b64087b755`.
- Skopeo runs with `--network none`, no Docker socket, a read-only OCI archive mount, and one writable run-owned scratch mount.
- No registry, Docker network, source rebuild, external candidate pull, daemon reconfiguration, publication, push, Git tag, signature, attestation, promotion, or release.
- Validate `linux/amd64`, strict SemVer, exact canonical refs, descriptor/config/blob sizes and SHA-256 values, five required OCI labels, config digest/image ID equality, and ordered config/Docker DiffID equality.
- Refuse pre-existing canonical tags and exact helper-name collisions. Register cleanup before every mutation and remove only run-owned targets.
- Temporary Docker archives are non-empty regular non-linked files no larger than `8 * 1024 * 1024 * 1024` bytes and are removed on success and failure.
- Preserve existing case-sensitive full/Docker-Hub-short missing-target handling and bounded combined primary/cleanup errors.

---

### Task 1: Implement the offline loader and runtime identity proof

**Files:**
- Modify: `scripts/release/load-oci-candidate.mjs`
- Modify: `tests/release-contract/load-oci-candidate.test.mjs`
- Modify: `tests/release-contract/release-candidate-fixture.mjs`

**Interfaces:**
- Consumes: existing `loadOciCandidate(options, dependencies)` options and the release-candidate fixture's OCI archive.
- Produces: unchanged CLI; `loadOciCandidate` resolves `{ ref, digest, imageId, diffIds }`, where `digest` is the certified OCI manifest digest, `imageId` is the verified OCI config digest, and `diffIds` is the verified ordered config/Docker DiffID array.
- Produces: injectable `createScratch`, `validateScratchArchive`, and `removeScratch` filesystem boundaries for deterministic process/cleanup tests; production defaults use `mkdtempSync`, regular non-link validation, and exact-root `rmSync`.
- Produces: `LOADER_CONTRACT` schema `cmsify.oci-loader.v1` with `skopeoImage` and `transport: "offline-docker-archive"`; it no longer exposes registry topology.

- [ ] **Step 1: Write failing OCI evidence tests**

Add focused tests that mutate one value at a time and assert no process boundary is reached:

```js
for (const [name, mutate, diagnostic] of [
  ["manifest bytes", ({ manifest }) => { manifest.layers[0].size += 1; }, /manifest.*digest|manifest.*size/i],
  ["config bytes", ({ config }) => { config.os = "windows"; }, /config.*digest|config.*platform/i],
  ["config media type", ({ manifest }) => { manifest.config.mediaType = "application/octet-stream"; }, /config.*media type/i],
  ["rootfs type", ({ config }) => { config.rootfs.type = "unknown"; }, /rootfs.*layers/i],
  ["unsafe DiffID", ({ config }) => { config.rootfs.diff_ids[0] = "sha256:not-a-digest"; }, /DiffID/i],
  ["version label", ({ config }) => { config.config.Labels["org.opencontainers.image.version"] = "9.9.9"; }, /version label/i],
]) {
  test(`rejects mismatched ${name} before Docker`, async () => {
    const root = createValidCandidate();
    try {
      mutateOciLayout(root, "api", mutate);
      const boundary = processBoundary();
      await assert.rejects(loadOciCandidate(options(root), { run: boundary.run, runId: RUN_ID }), diagnostic);
      assert.equal(boundary.calls.length, 0);
    } finally { removeCandidate(root); }
  });
}
```

Add separate one-value mutations for manifest descriptor size/digest, config descriptor size/digest, absent/duplicate selected blobs, wrong config OS/architecture, a non-array/empty/malformed DiffID list, and each required label: title, source, revision, version, and license.

- [ ] **Step 2: Run the evidence tests and verify RED**

Run:

```powershell
node --test --test-name-pattern="manifest bytes|config bytes|config media type|rootfs type|unsafe DiffID|required OCI label" tests/release-contract/load-oci-candidate.test.mjs
```

Expected: the new cases fail because the current loader reads only `index.json`/`oci-layout` and reaches the injected process boundary.

- [ ] **Step 3: Extend the bounded OCI tar reader**

Replace the index-only return with verified selected-manifest/config evidence. Use exact blob entry names derived only from validated SHA-256 descriptors, reject duplicates, and hash the bytes before JSON parsing:

```js
function sha256Digest(bytes) {
  return `sha256:${createHash("sha256").update(bytes).digest("hex")}`;
}

function validateBlobDescriptor(value, mediaType, label) {
  assert(value?.mediaType === mediaType, `${label} media type is invalid.`);
  assert(DIGEST.test(value?.digest ?? ""), `${label} digest must be sha256.`);
  assert(Number.isSafeInteger(value?.size) && value.size > 0 && value.size <= MAX_JSON_BYTES, `${label} size is unsafe.`);
  return value;
}
```

Read `blobs/sha256/<manifest hex>` and then `blobs/sha256/<config hex>` from the same already-open archive. Require byte length and `sha256Digest(bytes)` to equal each descriptor before `JSON.parse`. Validate OCI schema 2, config media type `application/vnd.oci.image.config.v1+json`, every layer descriptor's OCI/Docker layer media type, `linux/amd64`, `rootfs.type === "layers"`, a non-empty ordered array of SHA-256 DiffIDs, and exact labels/source SHA from the release manifest.

- [ ] **Step 4: Run focused evidence tests and verify GREEN**

Run the Step 2 command.

Expected: all selected tests pass and every invalid archive produces zero process-boundary calls.

- [ ] **Step 5: Write failing offline-transport behavior tests**

Replace the registry-oriented happy-path expectations with exact offline commands:

```js
assert.deepEqual(result, {
  ref: boundary.canonicalRef,
  digest: manifest.oci.api.digest,
  imageId: boundary.configDigest,
  diffIds: boundary.diffIds,
});

const copy = boundary.calls.find((call) => call.phase === "oci-loader-skopeo-copy");
assert.equal(copy.args.includes("--network"), true);
assert.equal(copy.args[copy.args.indexOf("--network") + 1], "none");
assert.equal(copy.args.some((arg) => arg === `oci-archive:/candidate.oci.tar:${VERSION}`), true);
assert.equal(copy.args.some((arg) => arg === `docker-archive:/scratch/candidate.docker.tar:${boundary.canonicalRef}`), true);
assert.equal(copy.args.some((arg) => /docker\.sock|docker_engine/i.test(arg)), false);

const load = boundary.calls.find((call) => call.phase === "oci-loader-docker-load");
assert.deepEqual(load.args.slice(0, 2), ["image", "load"]);
assert.equal(load.args.includes("--platform"), true);
assert.equal(load.args.includes("linux/amd64"), true);
```

Add mutations for Skopeo networking, writable source mount, absent scratch mount, wrong source/destination selector, direct OCI `docker load`, registry/network commands, external candidate pull, loaded `Id`, OS, architecture, tag, each required label, and each ordered `RootFS.Layers` entry.

- [ ] **Step 6: Run the transport tests and verify RED**

Run:

```powershell
node --test --test-name-pattern="offline Docker archive|network none|loaded image identity|scratch cleanup|create-then-throw" tests/release-contract/load-oci-candidate.test.mjs
```

Expected: failures show the current loader still creates networks/Registry, uses a registry destination, and expects a loopback RepoDigest.

- [ ] **Step 7: Implement run-owned scratch and offline conversion**

Use Node filesystem APIs directly; keep filesystem boundaries injectable for deterministic cleanup tests:

```js
const scratchRoot = createScratch(resolve(tmpdir(), "cmsify-oci-loader-"));
scratchCleanupIntent = true;
const dockerArchive = resolve(scratchRoot, "candidate.docker.tar");

await execute([
  "run", "--rm", "--pull=never", "--platform", "linux/amd64",
  "--name", skopeoName,
  "--network", "none",
  "--label", LABEL_OWNER,
  "--label", `${LABEL_RUN}=${runId}`,
  "--mount", `type=bind,source=${input.archivePath},target=/candidate.oci.tar,readonly`,
  "--mount", `type=bind,source=${scratchRoot},target=/scratch`,
  SKOPEO_IMAGE,
  "copy",
  `oci-archive:/candidate.oci.tar:${options.version}`,
  `docker-archive:/scratch/candidate.docker.tar:${input.canonicalRef}`,
], "oci-loader-skopeo-copy");

validateScratchArchive(dockerArchive, scratchRoot, 8 * 1024 * 1024 * 1024);
canonicalCleanupIntent = true;
await execute(["image", "load", "--input", dockerArchive, "--platform", "linux/amd64"], "oci-loader-docker-load");
```

Remove registry constants, registry waits, networks, intermediate loopback refs, and registry cleanup. Keep the pinned Skopeo image pull. Validate scratch path ancestors and file identity before load; reject symlinks/reparse points, files outside the exact root, zero length, unsafe integers, and files over 8 GiB.

- [ ] **Step 8: Implement loaded runtime verification and cleanup**

Inspect only the exact canonical ref and validate evidence, not Docker load text:

```js
const loaded = parseDockerJson(
  await execute(["image", "inspect", "--format", "{{json .}}", input.canonicalRef], "oci-loader-canonical-inspect"),
  "canonical candidate inspection",
);
assert(loaded.Id === input.configDigest, `Loaded image ID must equal OCI config digest ${input.configDigest}.`);
assert(loaded.Os === "linux" && loaded.Architecture === "amd64", "Loaded candidate must be linux/amd64.");
assert(Array.isArray(loaded.RootFS?.Layers)
  && loaded.RootFS.Layers.length === input.diffIds.length
  && loaded.RootFS.Layers.every((value, index) => value === input.diffIds[index]), "Loaded RootFS DiffIDs must equal OCI config order.");
assert(Array.isArray(loaded.RepoTags) && loaded.RepoTags.includes(input.canonicalRef), "Loaded candidate must have the exact canonical tag.");
```

Validate all five labels from `loaded.Config.Labels`. In `finally`, remove the exact canonical tag on failure after load, remove an exact lingering Skopeo name, and delete only the validated run-owned scratch root. Scratch cleanup runs on success too; the canonical tag remains only on success.

- [ ] **Step 9: Run focused and full loader tests**

Run:

```powershell
node --check scripts/release/load-oci-candidate.mjs
node --test tests/release-contract/load-oci-candidate.test.mjs
```

Expected: syntax passes; every loader test passes with no skipped/cancelled tests.

- [ ] **Step 10: Commit Task 1**

```powershell
git add scripts/release/load-oci-candidate.mjs tests/release-contract/load-oci-candidate.test.mjs tests/release-contract/release-candidate-fixture.mjs
git commit -m "Load OCI candidates through offline transport"
```

---

### Task 2: Enforce the offline transport in workflow policy and runbooks

**Files:**
- Modify: `scripts/release/verify-release-contract.mjs`
- Modify: `tests/release-contract/verify-release-contract.test.mjs`
- Modify: `docs/release-runbook.md`
- Modify: `tests/upgrade/README.md`
- Modify: `tests/release-smoke/README.md`

**Interfaces:**
- Consumes: Task 1 `LOADER_CONTRACT.transport === "offline-docker-archive"` and stable loader CLI.
- Produces: semantic policy that rejects registry/network loading, candidate rebuilds/pulls, Docker socket mounts, connected Skopeo, and certification/upload of transport scratch.

- [ ] **Step 1: Write failing semantic mutation tests**

Add isolated mutations, each of which must make `verify-release-contract.mjs` exit non-zero:

```js
const mutations = [
  ["Skopeo network access", (source) => source.replace('"--network", "none"', '"--network", importerNetworkName')],
  ["registry relay", (source) => source.replace('"image", "load"', '"image", "pull"')],
  ["Docker socket", (source) => source.replace('"--network", "none"', '"--mount", "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock"')],
  ["direct OCI load", (source) => source.replace('"--input", dockerArchive', '"--input", input.archivePath')],
  ["missing DiffID proof", (source) => source.replace('loaded.RootFS?.Layers', 'input.diffIds')],
];
```

Also mutate the publish workflow to upload a scratch path and mutate each runbook to claim that a registry relay or rebuilt image is certified.

- [ ] **Step 2: Run semantic tests and verify RED**

Run:

```powershell
node --test tests/release-contract/verify-release-contract.test.mjs
```

Expected: only the new mutation cases fail with `contract mutation unexpectedly passed` or the new offline-transport contract diagnostic.

- [ ] **Step 3: Implement semantic verification**

Replace registry-topology assertions with checks for the exact offline trust boundary:

```js
expect(loaderSource.includes('transport: "offline-docker-archive"'), "OCI loader must declare offline Docker-archive transport.");
expect(loaderSource.includes('"--network", "none"'), "Skopeo must run without network access.");
expect(loaderSource.includes("docker-archive:/scratch/candidate.docker.tar:"), "Skopeo must write only disposable Docker transport scratch.");
expect(loaderSource.includes('["image", "load", "--input", dockerArchive, "--platform", "linux/amd64"]'), "Docker must load the scratch Docker archive for linux/amd64.");
expect(!/REGISTRY_IMAGE|registry-port|network-create|docker:\/\/.*registry/i.test(loaderSource), "OCI loader must not use a registry relay.");
```

Retain existing workflow sequencing checks that require loader invocation before accessibility, upgrade, and smoke, and reject direct build/pull/tag commands between load and rehearsal.

- [ ] **Step 4: Update runbooks**

State exactly that pinned Skopeo converts the verified OCI candidate offline into run-owned Docker transport scratch; Docker loads it; config digest, ordered DiffIDs, platform, labels, and canonical tag are verified; scratch is deleted and is never checksummed/uploaded/promoted. Remove all registry, relay-network, and destination-RepoDigest claims.

- [ ] **Step 5: Run focused policy tests and verifier**

Run:

```powershell
node --check scripts/release/verify-release-contract.mjs
node --test tests/release-contract/verify-release-contract.test.mjs
node scripts/release/verify-release-contract.mjs
git diff --check
```

Expected: every command exits 0 and all semantic tests pass.

- [ ] **Step 6: Run the complete Node release-policy suite**

Run:

```powershell
node --test --test-reporter=dot tests/upgrade/unit/*.test.mjs tests/release-contract/*.test.mjs
```

Expected: every test passes with no failures, skips, or cancellations.

- [ ] **Step 7: Commit Task 2**

```powershell
git add scripts/release/verify-release-contract.mjs tests/release-contract/verify-release-contract.test.mjs docs/release-runbook.md tests/upgrade/README.md tests/release-smoke/README.md
git commit -m "Enforce offline OCI candidate transport"
```

---

### Task 3: Certify the transport against the preserved API candidate

**Files:**
- Update ignored evidence: `.superpowers/sdd/2026-08-29-v1-release-certification-governance/task-8-report.md`
- Update ignored ledger: `.superpowers/sdd/2026-08-29-v1-release-certification-governance/progress.md`
- Remove ignored temporary fixture after the run: `artifacts/task8-live-loader-a8e2218/`

**Interfaces:**
- Consumes: independently reviewed Task 1/2 commits and preserved candidate version `1.0.0-task12.a8e2218`.
- Produces: live evidence binding the original archive/metadata hashes, returned manifest/config/DiffID identity, exact image labels, and complete cleanup.

- [ ] **Step 1: Bind preconditions**

Verify clean tracked status, exact source HEAD, Docker 29.7.2 availability, absent canonical candidate tag, absent loader-labeled containers, and these immutable inputs:

```text
archive  535ccd85ae5ced158d396534231f0d32e4ada2add63eb089a499b07547236488
metadata 81be3d015cc3e67c86221a858bcef8550dc8abdc5c8f4969d8a9d609fefd35f3
manifest 0d6022f0d4d40ec2232d8195885c46fc9b0cd827b791c904a85e8b1e78f4b042
```

- [ ] **Step 2: Run the loader exactly once**

Run:

```powershell
node scripts/release/load-oci-candidate.mjs load --archive artifacts/task12-candidate/oci/cmsify-api.oci.tar --manifest artifacts/task8-live-loader-a8e2218/live-release-manifest.json --kind api --version 1.0.0-task12.a8e2218
```

Expected: exit 0 with bounded JSON containing the canonical ref, certified OCI manifest digest, OCI config/Docker image digest, and ordered DiffIDs.

- [ ] **Step 3: Inspect runtime identity**

Run `docker image inspect` for only `docker.io/syntaxcircus/cmsify-api:1.0.0-task12.a8e2218`. Require `linux/amd64`, exact five labels, image ID equal to the returned config digest, exact ordered `RootFS.Layers`, and the exact canonical tag.

- [ ] **Step 4: Audit and clean exact run-owned state**

Recompute all three input hashes. Remove only the exact old canonical candidate tag, then verify no loader-labeled container, scratch directory, registry/network resource, or candidate tag remains. Resolve the exact fixture root beneath `artifacts/`, verify it is non-linked and contains only the temporary manifest, remove that exact root, and confirm absence.

- [ ] **Step 5: Record evidence and resume the master completion gate**

Append exact sanitized commands/results to the Task 8 report and completion ledger. Do not commit ignored evidence. If live validation succeeds, resume the master Task 8 candidate tuple only after the exact resilience prerelease becomes publicly available; do not rebuild a partial final tuple.
