# Moving-Baseline Upgrade and Rollback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a deterministic, versioned `0.1.3` PostgreSQL/media fixture and a fail-closed Docker rehearsal that proves v1 upgrade and matched-backup rollback before release promotion.

**Architecture:** A dependency-free Node 22 CLI validates fixture provenance and checksums, safely orchestrates digest-pinned PostgreSQL/MinIO/historical API containers, applies the normal candidate startup migration path, and asserts baseline, upgraded, and restored behavior. A dedicated CI workflow and the existing unified release workflow both consume the same fixture and rehearsal command; release promotion also verifies that the fixture represents the latest stable `0.1.x` release published before the candidate.

**Tech Stack:** Node.js 22 ESM and `node:test`, Docker Engine/Compose v2, PostgreSQL 17, MinIO S3 compatibility, ASP.NET Core/.NET 10, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md`

## Global Constraints

- Preserve all existing branch work and do not revisit remediation Tasks 1–8 except where Task 9 must integrate with their public behavior.
- Use Node 22 in CI and only Node built-ins in `eng/upgrade-tests`; do not add a root npm dependency graph.
- Run on Windows PowerShell and Linux CI; pass subprocess arguments as arrays and never construct shell command strings from fixture data.
- Pin the historical API to `docker.io/syntaxcircus/cmsify-api:0.1.3@sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931` (`linux/amd64`, source `bc652aec1acad7ef440576b5019a0fe7c72004b3`).
- Pin PostgreSQL to `docker.io/library/postgres:17-alpine@sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee` (`linux/amd64`).
- Pin MinIO to `docker.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2` (`linux/amd64`).
- The checked-in fixture is synthetic and must contain no real user data, customer domains, deployable credentials, or outbound webhook destination.
- Treat PostgreSQL and media as one backup generation; rollback always discards upgraded state and restores both into fresh volumes.
- Do not implement EF down-migrations or start `0.1.3` against a database written by v1.
- All Docker resources must carry both the generated run ID and `io.syntaxcircus.cmsify.upgrade-test=true`; cleanup may target only resources matching both.
- All timeouts are bounded, diagnostics are allow-listed and sanitized, and no global Docker prune is permitted.
- Use `apply_patch` for source, workflow, fixture-text, and documentation edits. Generate `database.sql`, media bytes, and checksums only through the checked-in generator command.
- Each task follows red-green-refactor, passes its focused checks, and ends with the listed commit before the next task begins.

---

## File Map

### Harness implementation

- `eng/upgrade-tests/cli.mjs` — argument parsing and the four public commands: `verify-fixture`, `generate-fixture`, `rehearse`, and `verify-release-baseline`.
- `eng/upgrade-tests/manifest.mjs` — fixture manifest schema, semantic validation, required-scenario contract, and immutable image parsing.
- `eng/upgrade-tests/checksums.mjs` — canonical SHA-256 inventory generation and verification.
- `eng/upgrade-tests/paths.mjs` — repository containment, safe run IDs, diagnostics paths, and Docker naming.
- `eng/upgrade-tests/process.mjs` — cancellable, bounded subprocess execution with argument arrays and redaction.
- `eng/upgrade-tests/docker.mjs` — label-scoped Compose lifecycle, readiness, logs, image identity, backup, restore, and cleanup.
- `eng/upgrade-tests/http.mjs` — bounded authenticated HTTP requests and byte/hash assertions.
- `eng/upgrade-tests/assertions.mjs` — shared baseline assertions plus candidate-only migration/lifecycle/canary assertions.
- `eng/upgrade-tests/fixture.mjs` — published-baseline seeding, normalized export, deterministic comparison, and fixture verification.
- `eng/upgrade-tests/rehearsal.mjs` — explicit phase machine for baseline, upgrade, and restored rollback.
- `eng/upgrade-tests/release-baseline.mjs` — latest already-published stable `0.1.x` discovery and GitHub/Docker provenance comparison.

### Harness tests and artifacts

- `tests/upgrade/unit/manifest.test.mjs` — manifest, coverage, image, and provenance cases.
- `tests/upgrade/unit/checksums.test.mjs` — canonical inventories and tamper detection.
- `tests/upgrade/unit/paths.test.mjs` — containment and exact cleanup selection.
- `tests/upgrade/unit/process.test.mjs` — argument safety, deadlines, cancellation, and redaction.
- `tests/upgrade/unit/rehearsal.test.mjs` — phase ordering, mandatory-phase failure, backup fencing, and cleanup.
- `tests/upgrade/unit/release-baseline.test.mjs` — stable release selection and moving-baseline rules.
- `tests/upgrade/integration/rehearsal.test.mjs` — opt-in full Docker rehearsal invoked by CI and release certification.
- `tests/upgrade/compose.yml` — digest-pinned PostgreSQL, MinIO, historical API, and candidate service topology.
- `tests/upgrade/seed/v0.1.3.sql` — deterministic historical-only state seeding after the published API creates its schema.
- `tests/upgrade/fixtures/v0.1.3/manifest.json` — authoritative fixture/provenance contract.
- `tests/upgrade/fixtures/v0.1.3/expected.json` — stable scenario IDs and baseline/candidate expectations.
- `tests/upgrade/fixtures/v0.1.3/database.sql` — normalized plain SQL export generated from published `0.1.3`.
- `tests/upgrade/fixtures/v0.1.3/media/...` — generated media payloads beneath exact object keys.
- `tests/upgrade/fixtures/v0.1.3/SHA256SUMS` — canonical checksum inventory.
- `tests/upgrade/README.md` — fixture refresh and harness usage.

### Integration and documentation

- `.github/workflows/upgrade-rollback.yml` — branch, push, and manual full rehearsal.
- `.github/workflows/publish-cmsify.yml` — certify the exact release OCI candidate with the rehearsal before promotion.
- `.github/workflows/dotnet-test.yml` — run fast upgrade-harness and fixture checks on every branch validation.
- `scripts/release/validate-release-tag.mjs` — remove the preliminary per-version-file rule; defer moving-baseline policy to the authoritative verifier.
- `scripts/release/verify-release-contract.mjs` — structurally enforce workflow dependencies and commands.
- `tests/release-contract/validate-release-tag.test.mjs` — tag validation after removal of the preliminary gate.
- `tests/release-contract/verify-release-contract.test.mjs` — positive and negative Task 9 release-workflow contracts.
- `docs/operations.md` — exact fixture, rehearsal, and matched-backup rollback runbook.
- `docs/v1-release-readiness.md` — point the v1 gate at committed Task 9 evidence.
- `docs/v1-release-remediation-handoff.md` — record final Task 9 artifacts and verification evidence.

## Shared Interfaces

All modules use plain frozen JavaScript objects documented with these JSDoc shapes; task implementations must keep these property names stable:

```js
/** @typedef {{repository:string, tag:string, digest:string, platform:"linux/amd64"}} ImmutableImage */
/** @typedef {{
 * schemaVersion:1,
 * baseline:{version:"0.1.3", sourceSha:string, apiImage:ImmutableImage,
 *   postgresImage:ImmutableImage, minioImage:ImmutableImage},
 * requiredFiles:string[], requiredScenarios:string[], expectedDataFile:"expected.json"
 * }} FixtureManifest */
/** @typedef {{
 * runId:string, projectName:string, repositoryRoot:string,
 * diagnosticsDirectory:string,
 * labels:{"io.syntaxcircus.cmsify.upgrade-test":"true","io.syntaxcircus.cmsify.upgrade-run":string}
 * }} RunScope */
/** @typedef {{
 * cwd?:string, env?:Record<string,string>, timeoutMs:number,
 * signal?:AbortSignal, phase?:string, redact?:string[]
 * }} ProcessOptions */
/** @typedef {{exitCode:number, stdout:string, stderr:string, durationMs:number}} ProcessResult */
/** @typedef {{
 * fixture:FixtureManifest, expected:object, docker:object,
 * apiBaseUrl:string, token:string, phase:"baseline"|"candidate"|"rollback",
 * canaryId?:string
 * }} AssertionContext */
/** @typedef {{name:string, scenario:string, status:"passed"|"failed", detail?:string}} AssertionResult */
/** @typedef {{phase:string, assertions:AssertionResult[]}} AssertionReport */
/** @typedef {{
 * repositoryRoot:string, fixtureDirectory:string, candidateImage:string,
 * candidateVersion:string, candidateSourceSha:string,
 * runId?:string, keepDiagnostics?:boolean, signal?:AbortSignal
 * }} RehearsalOptions */
/** @typedef {{
 * runId:string, result:"passed"|"failed", fixtureDigest:string,
 * baselineImage:ImmutableImage, candidate:{reference:string,imageId:string,version:string,sourceSha:string},
 * phases:Array<{name:string,status:"pending"|"running"|"passed"|"failed",startedAt?:string,finishedAt?:string}>
 * }} RehearsalReport */
/** @typedef {{version:string, tag:string, sourceSha:string, publishedAt:string}} PublishedRelease */
/** @typedef {{baselineVersion:string, sourceSha:string, apiDigest:string, verifiedAt:string}} VerificationResult */
```

---

### Task 1: Fixture manifest, coverage, and checksum verifier

**Files:**
- Create: `eng/upgrade-tests/manifest.mjs`
- Create: `eng/upgrade-tests/checksums.mjs`
- Create: `eng/upgrade-tests/cli.mjs`
- Create: `tests/upgrade/unit/manifest.test.mjs`
- Create: `tests/upgrade/unit/checksums.test.mjs`

**Interfaces:**
- Produces: `loadFixtureManifest(fixtureDirectory: string): FixtureManifest`.
- Produces: `validateFixtureManifest(manifest: unknown, fixtureDirectory: string): FixtureManifest`.
- Produces: `verifyFixtureChecksums(fixtureDirectory: string, manifest: FixtureManifest): Promise<Map<string,string>>`.
- Produces: `writeFixtureChecksums(fixtureDirectory: string, relativeFiles: string[]): Promise<string>`.
- Produces: CLI `node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3`.

- [ ] **Step 1: Write manifest tests that describe the complete contract**

Use deterministic temporary directories and assert the exact baseline identity, required files, required scenarios, canonical platform, and rejection cases:

```js
test("accepts the immutable v0.1.3 fixture contract", () => {
  const manifest = validateFixtureManifest(validManifest(), fixtureDirectory);
  assert.equal(manifest.baseline.version, "0.1.3");
  assert.equal(manifest.baseline.sourceSha, "bc652aec1acad7ef440576b5019a0fe7c72004b3");
  assert.equal(manifest.baseline.apiImage.digest, "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931");
  assert.deepEqual(new Set(manifest.requiredScenarios), REQUIRED_SCENARIOS);
});

for (const mutate of [absoluteFile, escapingFile, tagDigestMismatch, missingScenario, duplicateFile, unknownSchema]) {
  test(`rejects ${mutate.name}`, () => assert.throws(() => validateFixtureManifest(mutate(validManifest()), fixtureDirectory)));
}
```

`REQUIRED_SCENARIOS` is exactly `workspaces`, `permissions`, `templates`, `components`, `choice-revisions`, `content-versions`, `schedules`, `media`, `webhooks`, `audit`, `authentication`, and `provenance`.

- [ ] **Step 2: Run the manifest tests and verify RED**

Run:

```powershell
node --test tests/upgrade/unit/manifest.test.mjs
```

Expected: FAIL with `ERR_MODULE_NOT_FOUND` for `eng/upgrade-tests/manifest.mjs`.

- [ ] **Step 3: Implement strict manifest parsing and image validation**

Define immutable image objects as `{ repository, tag, digest, platform }`. Require `schemaVersion: 1`, canonical SemVer `0.1.3`, full lowercase source SHA, `linux/amd64`, `sha256:` plus 64 lowercase hex characters, sorted unique file paths, and exact required scenario coverage. Resolve every declared file against `fixtureDirectory` and reject any path for which `relative(fixtureDirectory, resolved)` starts with `..` or is absolute.

The valid-manifest test fixture must contain these concrete image identities, and Task 3 uses the same object for the checked-in manifest:

```json
{
  "schemaVersion": 1,
  "baseline": {
    "version": "0.1.3",
    "sourceSha": "bc652aec1acad7ef440576b5019a0fe7c72004b3",
    "apiImage": {
      "repository": "docker.io/syntaxcircus/cmsify-api",
      "tag": "0.1.3",
      "digest": "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931",
      "platform": "linux/amd64"
    },
    "postgresImage": {
      "repository": "docker.io/library/postgres",
      "tag": "17-alpine",
      "digest": "sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee",
      "platform": "linux/amd64"
    },
    "minioImage": {
      "repository": "docker.io/minio/minio",
      "tag": "RELEASE.2025-09-07T16-13-09Z",
      "digest": "sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2",
      "platform": "linux/amd64"
    }
  }
}
```

- [ ] **Step 4: Write checksum tests for canonical order, tampering, missing files, and extras**

```js
test("rejects an unlisted payload", async () => {
  await materializeFixture(root);
  await writeFile(resolve(root, "media", "unexpected.bin"), "x");
  await assert.rejects(() => verifyFixtureChecksums(root, manifest), /unlisted fixture payload: media\/unexpected\.bin/i);
});

test("writes ordinal forward-slash SHA256SUMS", async () => {
  const text = await writeFixtureChecksums(root, ["media/z.txt", "database.sql", "media/a.txt"]);
  assert.deepEqual(text.split("\n").filter(Boolean).map(line => line.slice(66)), ["database.sql", "media/a.txt", "media/z.txt"]);
});
```

- [ ] **Step 5: Run checksum tests and verify RED**

Run: `node --test tests/upgrade/unit/checksums.test.mjs`

Expected: FAIL because `checksums.mjs` does not exist.

- [ ] **Step 6: Implement checksum generation and verification**

Walk files without following symlinks, normalize separators to `/`, exclude only `SHA256SUMS`, hash bytes with `createHash("sha256")`, require exactly two spaces between digest and path, and compare the declared and actual file sets in both directions. Return verified hashes only after every check passes.

- [ ] **Step 7: Add the `verify-fixture` CLI command and focused checks**

The command loads the manifest, validates `expected.json` scenario IDs, verifies checksums, prints one success line, and returns nonzero with one sanitized error per failure. Test it through `spawnSync(process.execPath, [cli, ...arguments])` against a complete temporary fixture so process exit behavior is covered without creating an incomplete production fixture.

Run:

```powershell
node --test tests/upgrade/unit/manifest.test.mjs tests/upgrade/unit/checksums.test.mjs
```

Expected: all unit and temporary-fixture CLI tests PASS.

- [ ] **Step 8: Commit the verifier slice**

```powershell
git add eng/upgrade-tests/cli.mjs eng/upgrade-tests/manifest.mjs eng/upgrade-tests/checksums.mjs tests/upgrade/unit/manifest.test.mjs tests/upgrade/unit/checksums.test.mjs
git commit -m "Add upgrade fixture contract verifier"
```

---

### Task 2: Safe process, paths, Docker ownership, and cleanup

**Files:**
- Create: `eng/upgrade-tests/paths.mjs`
- Create: `eng/upgrade-tests/process.mjs`
- Create: `eng/upgrade-tests/docker.mjs`
- Create: `tests/upgrade/unit/paths.test.mjs`
- Create: `tests/upgrade/unit/process.test.mjs`

**Interfaces:**
- Consumes: immutable image objects from `manifest.mjs`.
- Produces: `createRunScope(repositoryRoot: string, requestedId?: string): RunScope` with `runId`, `projectName`, `diagnosticsDirectory`, and required labels.
- Produces: `runProcess(command: string, args: string[], options: ProcessOptions): Promise<ProcessResult>`.
- Produces: `createDockerHarness(scope: RunScope, executor = runProcess): DockerHarness`.
- `DockerHarness` exposes `up(services)`, `stop(service)`, `start(service)`, `exec(service,args)`, `logs()`, `inspectImage(image)`, `copyFrom`, `copyTo`, and `cleanup()`.

- [ ] **Step 1: Write containment and cleanup-selection tests**

```js
test("rejects diagnostics outside the repository-owned upgrade run root", () => {
  assert.throws(() => createRunScope(repositoryRoot, "..\\outside"), /safe run id/i);
});

test("cleanup filters on both ownership labels", async () => {
  const harness = createDockerHarness(scope, recordingExecutor);
  await harness.cleanup();
  assert.deepEqual(recordedDockerFilters, [
    "label=io.syntaxcircus.cmsify.upgrade-test=true",
    `label=io.syntaxcircus.cmsify.upgrade-run=${scope.runId}`
  ]);
});
```

- [ ] **Step 2: Run path tests and verify RED**

Run: `node --test tests/upgrade/unit/paths.test.mjs`

Expected: FAIL with missing `paths.mjs`.

- [ ] **Step 3: Implement run scope and containment**

Accept only lowercase `[a-z0-9][a-z0-9-]{7,47}` run IDs. Generate `cmsify-upgrade-<12 lowercase hex>` when omitted. Resolve diagnostics beneath `artifacts/upgrade-tests/<runId>` and prove containment with `relative`. Provide exact labels `io.syntaxcircus.cmsify.upgrade-test=true` and `io.syntaxcircus.cmsify.upgrade-run=<runId>`.

- [ ] **Step 4: Write bounded subprocess tests**

Cover literal argument preservation, timeout termination, abort-signal termination, output-size cap, nonzero exit, missing executable, and redaction of `CMSIFY_FIXTURE_TOKEN`, `POSTGRES_PASSWORD`, `MINIO_ROOT_PASSWORD`, and `Secrets__EncryptionKey` values.

```js
test("passes metacharacters as one literal argument", async () => {
  const result = await runProcess(process.execPath, [echoArgs, "x; Write-Output compromised"], { timeoutMs: 5_000 });
  assert.equal(JSON.parse(result.stdout)[0], "x; Write-Output compromised");
});
```

- [ ] **Step 5: Run process tests and verify RED**

Run: `node --test tests/upgrade/unit/process.test.mjs`

Expected: FAIL with missing `process.mjs`.

- [ ] **Step 6: Implement process execution and redaction**

Use `spawn(command, args, { shell: false, windowsHide: true })`. Cap captured stdout and stderr at 1 MiB each, kill the process tree on timeout/cancellation using platform-appropriate direct APIs, and return `{ exitCode, stdout, stderr, durationMs }`. Throw a typed `ProcessFailure` whose message contains the command basename, phase, exit code, and sanitized tail only.

- [ ] **Step 7: Write Docker command-construction tests before implementation**

Assert every Compose invocation supplies `--project-name`, `--file tests/upgrade/compose.yml`, the run env file, and no `down --volumes` wildcard cleanup. Assert resource discovery uses both labels and explicit IDs are removed only after logs are captured.

- [ ] **Step 8: Implement the Docker harness**

Use `docker compose` for topology and `docker ps/network ls/volume ls --filter` for ownership discovery. Verify historical/external images by `docker image inspect` platform and repo digest. Cleanup order is diagnostics/logs, containers, network, volumes, then run env file. Treat an already absent owned resource as success, but fail if a discovered resource lacks either required label.

- [ ] **Step 9: Run focused tests and commit**

```powershell
node --test tests/upgrade/unit/paths.test.mjs tests/upgrade/unit/process.test.mjs
git add eng/upgrade-tests/paths.mjs eng/upgrade-tests/process.mjs eng/upgrade-tests/docker.mjs tests/upgrade/unit/paths.test.mjs tests/upgrade/unit/process.test.mjs
git commit -m "Add safe upgrade rehearsal orchestration"
```

---

### Task 3: Generate and check in the published `0.1.3` fixture

**Files:**
- Create: `eng/upgrade-tests/fixture.mjs`
- Create: `tests/upgrade/seed/v0.1.3.sql`
- Create: `tests/upgrade/compose.yml`
- Create: `tests/upgrade/fixtures/v0.1.3/manifest.json`
- Create: `tests/upgrade/fixtures/v0.1.3/expected.json`
- Create: `tests/upgrade/fixtures/v0.1.3/database.sql` (generator output)
- Create: `tests/upgrade/fixtures/v0.1.3/media/cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1-fixture.txt` (generator output)
- Create: `tests/upgrade/fixtures/v0.1.3/media/cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2-pixel.png` (generator output)
- Create: `tests/upgrade/fixtures/v0.1.3/SHA256SUMS` (generator output)
- Modify: `eng/upgrade-tests/cli.mjs`
- Modify: `.gitignore`
- Test: `tests/upgrade/unit/checksums.test.mjs`

**Interfaces:**
- Consumes: `createDockerHarness`, `loadFixtureManifest`, and `writeFixtureChecksums`.
- Produces: `generateFixture({ repositoryRoot, fixtureDirectory, keepDiagnostics }): Promise<FixtureGenerationResult>`.
- Produces: `compareFixtureTrees(firstDirectory, secondDirectory): Promise<void>`.
- Produces: CLI `generate-fixture --fixture ...` and `generate-fixture --fixture ... --check`.

- [ ] **Step 1: Add a deterministic-tree test and verify RED**

```js
test("reports the first byte-level fixture drift", async () => {
  await assert.rejects(
    () => compareFixtureTrees(first, second),
    /fixture drift: database\.sql/i
  );
});
```

Run: `node --test tests/upgrade/unit/checksums.test.mjs`

Expected: FAIL because `compareFixtureTrees` is not exported.

- [ ] **Step 2: Implement deterministic comparison and the generator phase shell**

The generator creates two clean run scopes for `--check`, generates into temporary directories, verifies both trees are byte-identical, then compares the second tree with checked-in files. It refuses to overwrite the fixture until generation, baseline assertions, normalization, and checksums all succeed.

- [ ] **Step 3: Define the digest-pinned Compose topology**

Use PostgreSQL, MinIO, `baseline-api`, and `candidate-api` profiles on a Compose network declared `internal: true`, so neither API can reach an external webhook destination. Apply both ownership labels to every service, network, and named volume. Configure S3 bucket `cmsify-upgrade`, access key `cmsify-fixture-access`, and a synthetic password supplied through the run env file. Disable webhook workers, disable secret rotation, and configure the fixture-only legacy encryption key plus the candidate keyring needed to read it. Do not publish fixed database or MinIO ports.

- [ ] **Step 4: Build the deterministic historical seed**

Use UUID families in `expected.json` so every relationship is readable:

```json
{
  "ids": {
    "primaryWorkspace": "11111111-1111-4111-8111-111111111111",
    "restrictedWorkspace": "11111111-1111-4111-8111-111111111112",
    "adminUser": "22222222-2222-4222-8222-222222222221",
    "editorUser": "22222222-2222-4222-8222-222222222222",
    "readerClient": "33333333-3333-4333-8333-333333333331",
    "template": "44444444-4444-4444-8444-444444444441",
    "component": "55555555-5555-4555-8555-555555555551",
    "choiceSet": "66666666-6666-4666-8666-666666666661",
    "draftContent": "77777777-7777-4777-8777-777777777771",
    "publishedContent": "77777777-7777-4777-8777-777777777772",
    "scheduledContent": "77777777-7777-4777-8777-777777777773",
    "expiredContent": "77777777-7777-4777-8777-777777777774",
    "textMedia": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1",
    "imageMedia": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2",
    "webhook": "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1",
    "audit": "cccccccc-cccc-4ccc-8ccc-ccccccccccc1"
  }
}
```

Start the published API on an empty database so its own migrations create the schema. Seed supported entities through its public API and record each returned UUID by semantic name. Stop the API, set every variable timestamp to its fixed fixture value, and export. `fixture.mjs` canonicalizes the returned UUIDs by replacing every exact textual occurrence in the SQL dump according to the semantic ID map before checksumming; it fails if an observed UUID is absent, appears only in an unsafe substring, or remains after canonicalization. Apply `v0.1.3.sql` only for immutable historical revisions, a fixed BCrypt API-token hash, deterministic legacy webhook ciphertext/delivery history, audit rows, and package provenance that cannot be produced deterministically through the public API. The SQL begins by asserting the exact 11 baseline migration IDs and aborts on any mismatch.

Use fixed UTC timestamps around `2026-08-20T12:00:00Z`. Include draft, published, future `PublishAt`, effective-range, and expired examples; two choice revisions where published content snapshots the older label; an inline acyclic component snapshot; two media rows in the historical schema; one webhook delivery that cannot be retried; and both allowed and denied workspace access.

- [ ] **Step 5: Generate deterministic media and export the fixture**

Generate the text bytes as UTF-8 `Cmsify v0.1.3 upgrade fixture\n`. Generate the PNG from one checked-in base64 constant in `fixture.mjs`, not from an image library. Upload both beneath the exact keys in `expected.json`, then export with `pg_dump --format=plain --no-owner --no-privileges --quote-all-identifiers --encoding=UTF8`. Normalize only known volatile header/comment lines and line endings; never reorder SQL statements semantically.

- [ ] **Step 6: Generate twice, verify baseline ownership, and check in outputs**

Run:

```powershell
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
```

Expected: all three PASS; `git status --short` shows only the intended generated fixture and source files.

- [ ] **Step 7: Ignore run-owned diagnostics, not fixtures**

Add `/artifacts/upgrade-tests/` and `/tests/upgrade/.runs/` to `.gitignore`. Confirm `git check-ignore` does not ignore any path below `tests/upgrade/fixtures/v0.1.3`.

- [ ] **Step 8: Commit the fixture slice**

```powershell
git add .gitignore eng/upgrade-tests/fixture.mjs eng/upgrade-tests/cli.mjs tests/upgrade/compose.yml tests/upgrade/seed/v0.1.3.sql tests/upgrade/fixtures/v0.1.3
git commit -m "Add published v0.1.3 upgrade fixture"
```

---

### Task 4: Shared API, SQL, and media invariant assertions

**Files:**
- Create: `eng/upgrade-tests/http.mjs`
- Create: `eng/upgrade-tests/assertions.mjs`
- Create: `tests/upgrade/unit/assertions.test.mjs`
- Modify: `eng/upgrade-tests/fixture.mjs`
- Modify: `tests/upgrade/fixtures/v0.1.3/expected.json`

**Interfaces:**
- Consumes: verified `expected.json`, a `DockerHarness`, API base URL, and fixture token.
- Produces: `requestJson(request: HttpRequest): Promise<HttpResponse>` and `requestBytes(request: HttpRequest): Promise<HttpResponse>`.
- Produces: `assertBaseline(context: AssertionContext): Promise<AssertionReport>`.
- Produces: `assertCandidate(context: AssertionContext): Promise<AssertionReport>`.
- Produces: `assertRollback(context: AssertionContext): Promise<AssertionReport>`; this calls the same baseline assertion registry and adds canary absence.

- [ ] **Step 1: Write assertion-registry tests and verify RED**

Create fake HTTP and SQL adapters. Require every scenario in `REQUIRED_SCENARIOS` to register at least one named assertion, require baseline and rollback to share the exact registry, and prove one mismatched media byte reports its asset ID and expected/actual SHA without logging payload bytes.

```js
test("rollback cannot use a weaker assertion set", () => {
  assert.deepEqual(assertionNames("rollback"), assertionNames("baseline"));
});
```

Run: `node --test tests/upgrade/unit/assertions.test.mjs`

Expected: FAIL with missing `assertions.mjs`.

- [ ] **Step 2: Implement bounded HTTP helpers**

Use `fetch` with `AbortSignal.timeout(5_000)`, explicit `redirect: "manual"`, no ambient credentials, and `Authorization: Bearer <fixture token>`. JSON helpers accept an exact set of expected statuses; byte helpers stream with a 10 MiB cap and hash incrementally. Error text includes method, sanitized URL path, status, correlation ID, and `traceId`, but not authorization or response bodies containing secrets.

- [ ] **Step 3: Implement shared baseline assertions**

Cover `/health/live`, `/health/ready`, `/api/v1/auth/me`, workspace list/detail and denied `404`, template/component/choice/content endpoints, versions, scheduled/effective/expired visibility, media list/detail/file bytes, webhook list/delivery history, and audit queries. Add SQL assertions for exact baseline migrations, foreign-key relationships, immutable revision counts, snapshot choice label, package provenance columns, and fixed timestamps.

- [ ] **Step 4: Implement candidate-only assertions**

Require all 14 current migrations through `20260827135736_AddMediaLifecycleReconciliation`; verify the v1 media lifecycle migration gives historical media `Available`, canonical provider `s3`, deterministic existing storage keys, and no spurious deletion intent. Verify webhook v1 ciphertext remains readable with the legacy key. Create a canary content item through the API, read it back, and record its ID in the run report.

- [ ] **Step 5: Run assertion and fixture tests**

```powershell
node --test tests/upgrade/unit/assertions.test.mjs tests/upgrade/unit/manifest.test.mjs tests/upgrade/unit/checksums.test.mjs
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
```

Expected: PASS.

- [ ] **Step 6: Commit the invariant slice**

```powershell
git add eng/upgrade-tests/http.mjs eng/upgrade-tests/assertions.mjs eng/upgrade-tests/fixture.mjs tests/upgrade/unit/assertions.test.mjs tests/upgrade/fixtures/v0.1.3/expected.json tests/upgrade/fixtures/v0.1.3/SHA256SUMS
git commit -m "Add upgrade invariant assertions"
```

---

### Task 5: Baseline, upgrade, backup, and rollback phase machine

**Files:**
- Create: `eng/upgrade-tests/rehearsal.mjs`
- Create: `tests/upgrade/unit/rehearsal.test.mjs`
- Modify: `eng/upgrade-tests/cli.mjs`
- Modify: `eng/upgrade-tests/docker.mjs`

**Interfaces:**
- Consumes: `createRunScope`, `createDockerHarness`, `verifyFixtureChecksums`, `assertBaseline`, `assertCandidate`, and `assertRollback`.
- Produces: `rehearse(options: RehearsalOptions): Promise<RehearsalReport>`.
- Produces: CLI `rehearse --fixture <dir> --candidate-image <ref> --candidate-version <semver> --candidate-source-sha <40hex>`.
- `RehearsalReport.phases` contains exactly `preflight`, `restore-fixture`, `baseline`, `backup`, `upgrade`, `candidate`, `backup-reverify`, `discard-upgraded-state`, `restore-backup`, `rollback`, `cleanup`.

- [ ] **Step 1: Write phase-order and mandatory-failure tests**

```js
test("never destroys upgraded state before re-verifying the matched backup", async () => {
  const events = await runWithFakes();
  assert.ok(events.indexOf("backup:verify-again") < events.indexOf("upgraded-volumes:remove"));
});

test("candidate failure still captures logs and cleans owned resources", async () => {
  await assert.rejects(() => runWithFakes({ fail: "candidate" }), /candidate invariant/i);
  assert.deepEqual(events.slice(-2), ["diagnostics:capture", "owned-resources:cleanup"]);
});
```

- [ ] **Step 2: Run rehearsal tests and verify RED**

Run: `node --test tests/upgrade/unit/rehearsal.test.mjs`

Expected: FAIL with missing `rehearsal.mjs`.

- [ ] **Step 3: Implement explicit phase transitions**

Represent phase states as `pending`, `running`, `passed`, or `failed`. Disallow skipping and re-entry. Write `report.json` atomically after every transition. Return success only if every mandatory phase passed and cleanup either passed or had no owned resources.

- [ ] **Step 4: Implement matched backup fencing**

Stop the baseline API, export PostgreSQL and media into `artifacts/upgrade-tests/<runId>/backup`, create `backup-manifest.json` with run ID, baseline version, database SHA, per-object media SHA, and creation time, then verify it. Before deleting upgraded volumes, read and verify the same manifest again. Restore into newly named volumes and reject any backup whose run ID or baseline differs.

- [ ] **Step 5: Implement candidate image identity and startup**

Inspect the candidate before mutation. Require `linux/amd64`, exact `org.opencontainers.image.version`, exact `org.opencontainers.image.revision`, and a stable local image ID. Pass the reference and ID through the run env file, start only `candidate-api`, and let its entrypoint perform migrations.

- [ ] **Step 6: Implement rollback isolation**

After backup re-verification, stop the candidate, remove only the upgraded PostgreSQL/media volumes, create fresh rollback volumes, restore both backup members, and start only the baseline API. Assert the candidate canary is absent and run the complete shared baseline registry.

- [ ] **Step 7: Add CLI process-level tests and focused validation**

Test missing arguments, malformed candidate identity, cancellation, and sanitized report output through `spawnSync`. Run:

```powershell
node --test tests/upgrade/unit/rehearsal.test.mjs tests/upgrade/unit/process.test.mjs tests/upgrade/unit/paths.test.mjs
```

Expected: PASS.

- [ ] **Step 8: Commit the phase machine**

```powershell
git add eng/upgrade-tests/rehearsal.mjs eng/upgrade-tests/cli.mjs eng/upgrade-tests/docker.mjs tests/upgrade/unit/rehearsal.test.mjs
git commit -m "Add upgrade and rollback phase machine"
```

---

### Task 6: Full exact-image Docker rehearsal and repeatability proof

**Files:**
- Create: `tests/upgrade/integration/rehearsal.test.mjs`
- Modify: `tests/upgrade/compose.yml`
- Modify: `eng/upgrade-tests/docker.mjs`
- Modify: `eng/upgrade-tests/rehearsal.mjs`
- Modify: `eng/upgrade-tests/assertions.mjs`
- Modify: `tests/upgrade/fixtures/v0.1.3/expected.json`
- Modify: `tests/upgrade/fixtures/v0.1.3/SHA256SUMS`

**Interfaces:**
- Consumes: public `rehearse()` and the checked-in fixture.
- Produces: one opt-in integration test selected by `CMSIFY_UPGRADE_TEST=1` and candidate identity environment variables.
- Produces: a passing rehearsal report containing exact baseline/candidate image identities and all phase results.

- [ ] **Step 1: Write the opt-in end-to-end test**

```js
test("published v0.1.3 upgrades to the candidate and restores rollback", {
  skip: process.env.CMSIFY_UPGRADE_TEST !== "1"
}, async () => {
  const first = await rehearse(optionsFromEnvironment());
  const second = await rehearse(optionsFromEnvironment());
  assert.equal(first.result, "passed");
  assert.equal(second.result, "passed");
  assert.equal(first.fixtureDigest, second.fixtureDigest);
});
```

- [ ] **Step 2: Build an exact local candidate and verify RED**

Run from PowerShell:

```powershell
$task9Sha = (git rev-parse HEAD).Trim()
docker build --platform linux/amd64 --build-arg BUILD_VERSION=1.0.0-task9 --build-arg BUILD_INFORMATIONAL_VERSION="1.0.0-task9+$task9Sha" --build-arg BUILD_SOURCE_REVISION=$task9Sha --tag syntaxcircus/cmsify-api:1.0.0-task9 --file src/Cmsify.Api/Dockerfile .
$env:CMSIFY_UPGRADE_TEST = '1'
$env:CMSIFY_UPGRADE_CANDIDATE_IMAGE = 'syntaxcircus/cmsify-api:1.0.0-task9'
$env:CMSIFY_UPGRADE_CANDIDATE_VERSION = '1.0.0-task9'
$env:CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA = $task9Sha
node --test tests/upgrade/integration/rehearsal.test.mjs
```

Expected: FAIL at the first real integration gap, with sanitized phase diagnostics retained under `artifacts/upgrade-tests`.

- [ ] **Step 3: Fix integration gaps without weakening assertions**

Iterate only on topology, readiness, fixture compatibility, backup/restore, and assertion defects. Do not skip a required scenario, replace exact media checks with row counts, or allow an old binary to touch upgraded volumes.

- [ ] **Step 4: Run the exact full rehearsal twice**

Repeat the command from Step 2. Expected: both runs PASS from fresh resources; no containers, networks, or volumes with the run labels remain afterward.

Verify cleanup:

```powershell
docker ps -a --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker volume ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker network ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
```

Expected: headers only and no owned resources.

- [ ] **Step 5: Re-run deterministic fixture validation**

```powershell
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
git diff --exit-code -- tests/upgrade/fixtures/v0.1.3
```

Expected: PASS and no fixture diff.

- [ ] **Step 6: Run focused application integration tests**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --verbosity minimal
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore --verbosity minimal
```

Expected: PASS, including PostgreSQL/MinIO Testcontainers coverage.

- [ ] **Step 7: Commit exact-image rehearsal support**

```powershell
git add eng/upgrade-tests tests/upgrade
git commit -m "Prove exact-image upgrade and rollback"
```

---

### Task 7: Moving-baseline release gate and CI workflows

**Files:**
- Create: `eng/upgrade-tests/release-baseline.mjs`
- Create: `tests/upgrade/unit/release-baseline.test.mjs`
- Create: `.github/workflows/upgrade-rollback.yml`
- Modify: `eng/upgrade-tests/cli.mjs`
- Modify: `scripts/release/validate-release-tag.mjs`
- Modify: `tests/release-contract/validate-release-tag.test.mjs`
- Modify: `scripts/release/verify-release-contract.mjs`
- Modify: `tests/release-contract/verify-release-contract.test.mjs`
- Modify: `.github/workflows/dotnet-test.yml`
- Modify: `.github/workflows/publish-cmsify.yml`

**Interfaces:**
- Produces: `selectLatestPublishedStable01(releases: PublishedRelease[], candidateVersion: string): PublishedRelease`.
- Produces: `verifyReleaseBaseline({ candidateVersion, fixtureManifest, githubReleases, dockerDescriptor }): VerificationResult`.
- Produces: CLI `verify-release-baseline --fixture <dir> --candidate-version <semver> [--github-token-env GITHUB_TOKEN]`.
- Consumes: the exact candidate OCI archive built once by `publish-cmsify.yml`.

- [ ] **Step 1: Write moving-baseline unit tests**

Cover stable versus prerelease filtering, candidate exclusion, `0.1.4` certification from published `0.1.3`, rejection of `0.1.5` when published `0.1.4` exists but the fixture still records `0.1.3`, v1 certification from latest published `0.1.x`, GitHub/Docker digest disagreement, no published baseline, rate-limit/error responses, and malformed registry descriptors.

```js
test("rejects stale fixture after 0.1.4 is published", () => {
  assert.throws(() => verifyReleaseBaseline({
    candidateVersion: "0.1.5",
    fixtureManifest: manifest("0.1.3"),
    githubReleases: [published("0.1.3"), published("0.1.4")],
    dockerDescriptor: descriptorFor("0.1.4")
  }), /fixture records 0\.1\.3 but latest published baseline is 0\.1\.4/i);
});
```

- [ ] **Step 2: Run baseline tests and verify RED**

Run: `node --test tests/upgrade/unit/release-baseline.test.mjs`

Expected: FAIL with missing `release-baseline.mjs`.

- [ ] **Step 3: Implement published-release and Docker descriptor verification**

Use bounded `fetch` requests to GitHub releases and the Docker Registry v2 token/manifest endpoints. Accept only explicit HTTP 200 responses, require the GitHub release to be non-draft/non-prerelease and match `v0.1.x`, resolve and peel `refs/tags/v<version>` through the GitHub Git refs/objects APIs to a full commit, and require Docker's linux/amd64 child digest and tag to match the fixture manifest. The current candidate is never included because it is not yet a published GitHub release.

- [ ] **Step 4: Replace the preliminary tag gate**

Delete `exceedsPublishedUpgradeBaseline` and the nonexistent `tests/upgrade/fixtures/<version>.json` check from `validate-release-tag.mjs`. Update its tests so syntactically valid `v0.1.4` passes tag parsing; moving-baseline enforcement belongs exclusively to `verify-release-baseline`, which has the published-release context needed to make the decision correctly.

- [ ] **Step 5: Add fast branch validation**

Extend `.github/workflows/dotnet-test.yml` Node 22 release-contract job to run:

```bash
node --test tests/upgrade/unit/*.test.mjs tests/release-contract/*.test.mjs
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node scripts/release/verify-release-contract.mjs
```

The fast branch job deliberately does not regenerate the Docker-backed fixture. The dedicated workflow performs byte-level `generate-fixture --check`, and release-contract tests require that command there.

- [ ] **Step 6: Add the dedicated exact-image workflow**

Create `upgrade-rollback.yml` with SHA-pinned checkout/setup actions, Node 22, Buildx, Docker layer caching, fixture verification, one linux/amd64 candidate build with version `1.0.0-ci.<run_number>` and source SHA labels, deterministic fixture check, and the opt-in full test. Trigger on `workflow_dispatch`, `main` pushes, and pull requests affecting migration files, persistence/configuration, API startup, media, `eng/upgrade-tests`, `tests/upgrade`, Dockerfiles, Compose, or release workflows. Upload sanitized `artifacts/upgrade-tests/**` on failure.

- [ ] **Step 7: Gate exact release candidates before promotion**

Add an `upgrade-rollback` job to `publish-cmsify.yml` that needs `resolve` and `build`, downloads the one candidate artifact, loads `artifacts/oci/cmsify-api.oci.tar`, runs `verify-release-baseline`, verifies the fixture, runs the full rehearsal against the loaded exact image, and uploads diagnostics on failure. Add it to `certify.needs`. Do not rebuild the candidate and do not move any publication command ahead of this dependency.

- [ ] **Step 8: Extend structural release-contract tests first, then verifier**

Negative fixtures must prove the verifier rejects: missing dedicated workflow, mutable action refs, missing path triggers, missing fixture check, rehearsal without deterministic check, release rehearsal that rebuilds the candidate, `certify` not depending on `upgrade-rollback`, missing diagnostics upload, and promotion still reachable without the gate. Then implement matching assertions in `verify-release-contract.mjs`.

- [ ] **Step 9: Run all Node and release checks**

```powershell
node --test tests/upgrade/unit/*.test.mjs tests/release-contract/*.test.mjs
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node scripts/release/verify-release-contract.mjs
```

Expected: PASS.

- [ ] **Step 10: Commit CI and release gating**

```powershell
git add .github/workflows eng/upgrade-tests/release-baseline.mjs eng/upgrade-tests/cli.mjs scripts/release tests/upgrade/unit/release-baseline.test.mjs tests/release-contract
git commit -m "Gate releases on upgrade rollback evidence"
```

---

### Task 8: Operator runbook, handoff, and complete validation

**Files:**
- Create: `tests/upgrade/README.md`
- Modify: `docs/operations.md`
- Modify: `docs/v1-release-readiness.md`
- Modify: `docs/v1-release-remediation-handoff.md`

**Interfaces:**
- Consumes: final CLI commands, fixture manifest, CI workflow, and verified test evidence.
- Produces: operator-visible refresh, rehearsal, rollback, failure-diagnosis, and older-prerelease procedures.

- [ ] **Step 1: Write the fixture and harness README**

Document prerequisites (Node 22, Docker Engine, Compose v2), exact `verify-fixture`, `generate-fixture --check`, candidate build, and `rehearse` commands for PowerShell and POSIX shells. Explain every fixture file, synthetic credential boundary, deterministic refresh after a stable `0.1.x` release, retained diagnostics, and label-scoped cleanup.

- [ ] **Step 2: Extend the production operations guide**

Add the supported sequence:

1. verify the fixture baseline and exact digests;
2. take a matched PostgreSQL/media backup and verify checksums;
3. rehearse with the exact candidate;
4. deploy while retaining the matched backup and prior image;
5. on failure, stop traffic, discard v1-written state, restore both backup members into clean storage, start the exact prior image, and validate readiness/content/media;
6. never run `0.1.3` against v1-written state;
7. installations older than `0.1.3` first upgrade to the recorded baseline, verify health and a new matched backup, then proceed to v1.

- [ ] **Step 3: Update readiness evidence and remediation handoff**

Link the fixture manifest, checksums, design, implementation plan, workflow, and runbook. Record actual candidate image ID/digest, baseline API/PostgreSQL/MinIO digests, fixture aggregate digest, commands, pass counts, and environment facts from the final run. Do not write claimed evidence before the associated command has passed.

- [ ] **Step 4: Run all fast Node validation**

```powershell
node --test tests/upgrade/unit/*.test.mjs tests/release-contract/*.test.mjs
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
node scripts/release/verify-release-contract.mjs
```

Expected: PASS.

- [ ] **Step 5: Run the exact full rehearsal again and retain evidence**

Build the final local candidate with its actual `git rev-parse HEAD`, set the four `CMSIFY_UPGRADE_*` variables from Task 6, and run:

```powershell
node --test tests/upgrade/integration/rehearsal.test.mjs
```

Expected: two clean PASS rehearsals, successful matched-backup rollback, and no owned Docker resources left behind.

- [ ] **Step 6: Run required focused .NET and TypeScript checks**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --verbosity minimal
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore --verbosity minimal
Set-Location sdk/typescript
npm ci
npm run generate:check
npm run typecheck
npm test
npm run build
Set-Location ../..
```

Expected: PASS; report Docker/Testcontainers or Node-version limitations rather than skipping them.

- [ ] **Step 7: Run full solution build and test**

```powershell
dotnet build Cmsify.slnx --configuration Release --no-restore
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

Expected: PASS. Record existing warnings separately and assign any Task 11 Admin nullable warnings to Task 11 rather than hiding them.

- [ ] **Step 8: Audit the written spec requirement by requirement**

Create a temporary checklist mapping every spec goal, fixture scenario, phase, invariant, error case, test, CI gate, security constraint, and acceptance criterion to a file plus fresh command output. Fix every missing or indirect item, rerun the affected checks, and update handoff evidence only after it passes.

- [ ] **Step 9: Check worktree hygiene and commit Task 9**

```powershell
git diff --check
git status --short
git add tests/upgrade/README.md docs/operations.md docs/v1-release-readiness.md docs/v1-release-remediation-handoff.md
git commit -m "Document upgrade rollback operations"
git status --short
```

Expected: final status is clean and the handoff contains exact fresh evidence.
