import { createHash } from "node:crypto";
import { lstat, mkdir, readdir } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

import { assertBaseline, assertCandidate, assertRollback, captureWebhookWorkerState } from "./assertions.mjs";
import { verifyFixtureChecksums } from "./checksums.mjs";
import { createDockerHarness } from "./docker.mjs";
import { loadExpectedData } from "./expected.mjs";
import { createDockerHttpAdapter } from "./http.mjs";
import { loadFixtureManifest } from "./manifest.mjs";
import { assertTrustedRunScope, createRunScope } from "./paths.mjs";
import { assertPhysicalPath, ensureSafeDirectory, openSafeRegularFile, readSafeFile, writeSafeAtomically } from "./safe-files.mjs";

export const REHEARSAL_PHASES = Object.freeze([
  "preflight",
  "restore-fixture",
  "baseline",
  "backup",
  "upgrade",
  "candidate",
  "backup-reverify",
  "discard-upgraded-state",
  "restore-backup",
  "rollback",
  "cleanup",
]);

const READINESS_TIMEOUT_MS = 120_000;
const CANDIDATE_SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?$/;
const CANDIDATE_SOURCE_SHA = /^[0-9a-f]{40}$/;
const FIXTURE_ENVIRONMENT = Object.freeze({
  POSTGRES_PASSWORD: "cmsify-fixture-postgres-only",
  MINIO_ROOT_PASSWORD: "cmsify-fixture-minio-only",
  CMSIFY_FIXTURE_ADMIN_PASSWORD: "Cmsify-fixture-admin-only-0.1.3!",
  CMSIFY_FIXTURE_ADMIN_PASSWORD_HASH: "fixture-only-existing-user-no-seed",
  CMSIFY_FIXTURE_LEGACY_KEY: "Q21zaWZ5IGZpeHR1cmUgbGVnYWN5IGtleSAwLjEuMyE=",
  CMSIFY_FIXTURE_LEGACY_KEY_BASE64: "Q21zaWZ5IGZpeHR1cmUgbGVnYWN5IGtleSAwLjEuMyE=",
  CMSIFY_FIXTURE_CANDIDATE_KEY_BASE64: "Q21zaWZ5IGZpeHR1cmUgY2FuZGlkYXRlIGtleSB2MSE=",
});

function messageOf(error) {
  return error instanceof Error ? error.message : "Upgrade rehearsal failed.";
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

/** Validates caller-supplied candidate syntax before Docker inspection. */
export function validateCandidateInput({ candidateImage, candidateVersion, candidateSourceSha }) {
  assert(typeof candidateImage === "string" && candidateImage.length > 0 && candidateImage.length <= 256 && !/[\s\r\n\0]/.test(candidateImage) && !candidateImage.startsWith("-") && !candidateImage.includes("://"), "Candidate image reference is malformed.");
  assert(typeof candidateVersion === "string" && !candidateVersion.includes("+"), "Candidate version build metadata is not accepted; source identity is appended by the rehearsal.");
  assert(CANDIDATE_SEMVER.test(candidateVersion), "Candidate version must be valid SemVer.");
  assert(typeof candidateSourceSha === "string" && CANDIDATE_SOURCE_SHA.test(candidateSourceSha), "Candidate source SHA must be exactly 40 lowercase hexadecimal characters.");
  return Object.freeze({ candidateImage, candidateVersion, candidateSourceSha });
}

function isContainedBy(parent, candidate) {
  const pathFromParent = relative(parent, candidate);
  return pathFromParent === "" || (!pathFromParent.startsWith(`..${sep}`) && pathFromParent !== ".." && !isAbsolute(pathFromParent));
}

function imageReference(image) {
  return `${image.repository}@${image.digest}`;
}

function verifiedFixtureDigest(checksums) {
  assert(checksums instanceof Map && checksums.size > 0, "Verified fixture checksums are required.");
  const entries = [...checksums.entries()].sort(([left], [right]) => Buffer.from(left).compare(Buffer.from(right)));
  for (const [file, digest] of entries) {
    assert(typeof file === "string" && file.length > 0 && typeof digest === "string" && /^[0-9a-f]{64}$/.test(digest), "Verified fixture checksum entry is invalid.");
  }
  const inventory = entries.map(([file, digest]) => `${digest}  ${file}\n`).join("");
  return createHash("sha256").update(inventory).digest("hex");
}

function throwIfCancelled(signal) {
  if (signal?.aborted) throw new Error("Upgrade rehearsal was cancelled.");
}

async function waitUntilReady(check, description, { signal, timeoutMs = READINESS_TIMEOUT_MS } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  let attempts = 0;
  while (Date.now() < deadline) {
    throwIfCancelled(signal);
    attempts += 1;
    try {
      await check();
      return Object.freeze({ service: description, status: "ready", attempts });
    } catch (error) {
      lastError = error;
    }
    try {
      await delay(1_000, undefined, signal ? { signal } : undefined);
    } catch {
      throwIfCancelled(signal);
      throw new Error(`${description} readiness wait failed.`);
    }
  }
  const failure = new Error(`${description} did not become ready within ${timeoutMs} milliseconds.`, { cause: lastError });
  failure.safeEvidence = { readiness: [{ service: description, status: "failed", attempts }] };
  throw failure;
}

function dockerOptions(context) {
  return {
    ...(context.signal ? { signal: context.signal } : {}),
    redact: context.redactions ?? [],
  };
}

async function waitForInfrastructure(context) {
  const options = dockerOptions(context);
  const postgres = await waitUntilReady(
    () => context.harness.exec("postgres", ["pg_isready", "--username", "cmsify", "--dbname", "cmsify"], options),
    "PostgreSQL",
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
  const minio = await waitUntilReady(
    () => context.harness.exec("minio", ["curl", "--silent", "--show-error", "--fail", "http://localhost:9000/minio/health/live"], options),
    "MinIO",
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
  return [postgres, minio];
}

async function waitForApi(context, service, description) {
  return waitUntilReady(
    () => context.harness.exec(service, ["curl", "--silent", "--show-error", "--fail", "http://localhost:8080/health/ready"], dockerOptions(context)),
    description,
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
}

function assertionFailureEvidence(error, readiness = []) {
  const match = messageOf(error).match(/^Invariant ([a-z0-9][a-z0-9-]{0,63}) failed:/);
  return {
    ...(readiness.length > 0 ? { readiness } : {}),
    ...(match ? { assertions: [{ name: match[1], status: "failed" }] } : {}),
  };
}

async function configureMedia(context) {
  const options = dockerOptions(context);
  await context.harness.exec("minio", [
    "mc", "alias", "set", "fixture", "http://localhost:9000", "cmsify-fixture-access", FIXTURE_ENVIRONMENT.MINIO_ROOT_PASSWORD,
  ], options);
  await context.harness.exec("minio", ["mc", "mb", "--ignore-existing", "fixture/cmsify-upgrade"], options);
}

async function restoreFixtureState(context) {
  throwIfCancelled(context.signal);
  await context.harness.up(["postgres", "minio"], dockerOptions(context));
  const readiness = await waitForInfrastructure(context);
  try {
    await context.harness.copyTo("postgres", resolve(context.fixtureDirectory, "database.sql"), "/tmp/cmsify-upgrade-fixture.sql", dockerOptions(context));
    await context.harness.exec("postgres", [
      "psql", "--username", "cmsify", "--dbname", "cmsify", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--file=/tmp/cmsify-upgrade-fixture.sql",
    ], dockerOptions(context));
    await configureMedia(context);
    await context.harness.copyTo("minio", `${resolve(context.fixtureDirectory, "media")}${sep}.`, "/tmp/cmsify-upgrade-fixture-media", dockerOptions(context));
    await context.harness.exec("minio", ["mc", "mirror", "--overwrite", "/tmp/cmsify-upgrade-fixture-media", "fixture/cmsify-upgrade"], dockerOptions(context));
    return readiness;
  } catch (error) {
    error.safeEvidence = { readiness };
    throw error;
  }
}

async function restoreBackupState(context) {
  throwIfCancelled(context.signal);
  await context.harness.up(["postgres", "minio"], dockerOptions(context));
  const readiness = await waitForInfrastructure(context);
  try {
    const backupDirectory = backupDirectoryFor(context.scope);
    await context.harness.copyTo("postgres", resolve(backupDirectory, "database.dump"), "/tmp/cmsify-matched-restore.dump", dockerOptions(context));
    await context.harness.exec("postgres", [
      "pg_restore", "--username", "cmsify", "--dbname", "cmsify", "--clean", "--if-exists",
      "--no-owner", "--no-privileges", "--exit-on-error", "/tmp/cmsify-matched-restore.dump",
    ], dockerOptions(context));
    await configureMedia(context);
    await context.harness.copyTo("minio", `${resolve(backupDirectory, "media")}${sep}.`, "/tmp/cmsify-matched-restore-media", dockerOptions(context));
    await context.harness.exec("minio", ["mc", "mirror", "--overwrite", "/tmp/cmsify-matched-restore-media", "fixture/cmsify-upgrade"], dockerOptions(context));
    return readiness;
  } catch (error) {
    error.safeEvidence = { readiness };
    throw error;
  }
}

function assertionDocker(context) {
  if (context.assertionDocker) return context.assertionDocker;
  context.assertionDocker = Object.freeze({
    exec(service, args, options = {}) {
      const redactions = [...new Set([...(context.redactions ?? []), ...(options.redact ?? [])])];
      return context.harness.exec(service, args, {
        ...options,
        ...(context.signal ? { signal: context.signal } : {}),
        redact: redactions,
      });
    },
  });
  return context.assertionDocker;
}

function assertionContext(context, service, extra = {}) {
  const docker = assertionDocker(context);
  return {
    fixture: context.manifest,
    expected: context.expected,
    ids: { ...context.expected.ids, ...context.expected.relatedIds },
    docker,
    apiBaseUrl: "http://localhost:8080",
    token: context.expected.authentication.readerToken,
    http: createDockerHttpAdapter(docker, service),
    signal: context.signal,
    webhookWorkerStateBeforeStart: context.webhookWorkerStateBeforeStart,
    runId: context.scope.runId,
    ...extra,
  };
}

function createDefaultOperations(context, dependencies = {}) {
  const loadManifest = dependencies.loadFixtureManifest ?? loadFixtureManifest;
  const loadExpected = dependencies.loadExpectedData ?? loadExpectedData;
  const verifyChecksums = dependencies.verifyFixtureChecksums ?? verifyFixtureChecksums;
  const makeHarness = dependencies.createDockerHarness ?? createDockerHarness;
  const baselineAssertions = dependencies.assertBaseline ?? assertBaseline;
  const candidateAssertions = dependencies.assertCandidate ?? assertCandidate;
  const rollbackAssertions = dependencies.assertRollback ?? assertRollback;
  const captureWorkerState = dependencies.captureWebhookWorkerState ?? captureWebhookWorkerState;
  const makeBackup = dependencies.createMatchedBackup ?? createMatchedBackup;
  const verifyBackup = dependencies.verifyMatchedBackup ?? verifyMatchedBackup;
  context.harness = makeHarness(context.scope);

  return {
    async preflight() {
      throwIfCancelled(context.signal);
      context.manifest = loadManifest(context.fixtureDirectory);
      context.expected = await loadExpected(context.fixtureDirectory, context.manifest);
      const fixtureChecksums = await verifyChecksums(context.fixtureDirectory, context.manifest);
      context.fixtureDigest = verifiedFixtureDigest(fixtureChecksums);
      context.redactions = [
        ...Object.values(FIXTURE_ENVIRONMENT),
        context.expected.authentication.readerToken,
        context.expected.authentication.adminPassword,
        context.candidateImage,
      ].filter((value) => typeof value === "string" && value.length > 0);
      await context.harness.inspectImage(context.manifest.baseline.apiImage, dockerOptions(context));
      await context.harness.inspectImage(context.manifest.baseline.postgresImage, dockerOptions(context));
      await context.harness.inspectImage(context.manifest.baseline.minioImage, dockerOptions(context));
      context.candidateIdentity = await context.harness.inspectCandidateImage(context.candidateImage, {
        version: context.candidateVersion,
        sourceSha: context.candidateSourceSha,
      }, dockerOptions(context));
      const prerequisites = await context.harness.verifyPrerequisites({
        postgresImage: context.manifest.baseline.postgresImage,
        minioImage: context.manifest.baseline.minioImage,
        baselineApiImage: context.manifest.baseline.apiImage,
        candidateImageId: context.candidateIdentity.imageId,
      }, dockerOptions(context));
      await context.harness.writeEnvironment({
        POSTGRES_IMAGE: imageReference(context.manifest.baseline.postgresImage),
        MINIO_IMAGE: imageReference(context.manifest.baseline.minioImage),
        BASELINE_API_IMAGE: imageReference(context.manifest.baseline.apiImage),
        CANDIDATE_API_IMAGE: context.candidateIdentity.imageId,
        CANDIDATE_API_IMAGE_REFERENCE: context.candidateIdentity.reference,
        CANDIDATE_API_IMAGE_ID: context.candidateIdentity.imageId,
        ...FIXTURE_ENVIRONMENT,
      });
      context.environmentWritten = true;
      context.fixtureIdentity = {
        baselineVersion: context.manifest.baseline.version,
        baselineSourceSha: context.manifest.baseline.sourceSha,
      };
      return {
        ...context.candidateIdentity,
        prerequisites,
        fixtureDigest: context.fixtureDigest,
        baselineImage: context.manifest.baseline.apiImage,
      };
    },

    async restoreFixture() {
      const readiness = await restoreFixtureState(context);
      return { readiness };
    },

    async baseline() {
      throwIfCancelled(context.signal);
      context.webhookWorkerStateBeforeStart = await captureWorkerState(assertionDocker(context), { ...context.expected.ids, ...context.expected.relatedIds });
      await context.harness.up(["baseline-api"], dockerOptions(context));
      const readiness = await waitForApi(context, "baseline-api", "baseline-api");
      try {
        const result = await baselineAssertions(assertionContext(context, "baseline-api"));
        return { readiness: [readiness], assertions: result.assertions };
      } catch (error) {
        error.safeEvidence = assertionFailureEvidence(error, [readiness]);
        throw error;
      }
    },

    async backup() {
      throwIfCancelled(context.signal);
      await context.harness.stop("baseline-api", dockerOptions(context));
      return makeBackup({
        harness: context.harness,
        scope: context.scope,
        baselineVersion: context.manifest.baseline.version,
        now: context.now,
        signal: context.signal,
        redact: context.redactions,
      });
    },

    async upgrade() {
      throwIfCancelled(context.signal);
      context.webhookWorkerStateBeforeStart = await captureWorkerState(assertionDocker(context), { ...context.expected.ids, ...context.expected.relatedIds });
      await context.harness.up(["candidate-api"], dockerOptions(context));
      const readiness = await waitForApi(context, "candidate-api", "candidate-api");
      return { readiness: [readiness] };
    },

    async candidate() {
      try {
        const result = await candidateAssertions(assertionContext(context, "candidate-api", { candidate: context.candidateIdentity }));
        assert(typeof result?.canaryId === "string" && result.canaryId.length > 0, "Candidate assertions did not return the required canary ID.");
        return result;
      } catch (error) {
        error.safeEvidence = assertionFailureEvidence(error);
        throw error;
      }
    },

    async backupReverify() {
      throwIfCancelled(context.signal);
      await context.harness.stop("candidate-api", dockerOptions(context));
      return verifyBackup({
        harness: context.harness,
        scope: context.scope,
        baselineVersion: context.manifest.baseline.version,
        manifestSha256: context.backup.manifestSha256,
        signal: context.signal,
        redact: context.redactions,
      });
    },

    async discardUpgradedState() {
      throwIfCancelled(context.signal);
      await context.harness.discardDataVolumes(dockerOptions(context), () => verifyBackup({
        harness: context.harness,
        scope: context.scope,
        baselineVersion: context.manifest.baseline.version,
        manifestSha256: context.backup.manifestSha256,
        signal: context.signal,
        redact: context.redactions,
      }));
      return { backupVerified: true, dataVolumesDiscarded: true };
    },

    async restoreBackup() {
      const readiness = await restoreBackupState(context);
      return { readiness };
    },

    async rollback() {
      throwIfCancelled(context.signal);
      context.webhookWorkerStateBeforeStart = await captureWorkerState(assertionDocker(context), { ...context.expected.ids, ...context.expected.relatedIds });
      await context.harness.up(["baseline-api"], dockerOptions(context));
      const readiness = await waitForApi(context, "baseline-api", "baseline-api");
      try {
        const result = await rollbackAssertions(assertionContext(context, "baseline-api", { canaryId: context.canaryId }));
        return { readiness: [readiness], assertions: result.assertions };
      } catch (error) {
        error.safeEvidence = assertionFailureEvidence(error, [readiness]);
        throw error;
      }
    },

    async captureDiagnostics() {
      return context.harness.logs({ ...dockerOptions(context), resourcesStarted: context.environmentWritten === true });
    },

    async cleanup() {
      return context.harness.cleanup({ redact: context.redactions ?? [], resourcesStarted: context.environmentWritten === true });
    },
  };
}

async function regularFileSha256(path, description, signal, safeRoot = resolve(path, "..")) {
  throwIfCancelled(signal);
  let handle;
  try {
    handle = await openSafeRegularFile(safeRoot, path);
  } catch {
    throw new Error(`Matched backup is missing ${description}.`);
  }
  try {
    const hash = createHash("sha256");
    const stream = handle.createReadStream({ autoClose: false });
    for await (const chunk of stream) {
      throwIfCancelled(signal);
      hash.update(chunk);
    }
    return hash.digest("hex");
  } finally {
    await handle.close();
  }
}

async function mediaInventory(root, directory = root, signal) {
  throwIfCancelled(signal);
  let stat;
  try {
    stat = await lstat(directory);
  } catch {
    throw new Error("Matched backup media directory is missing.");
  }
  await assertPhysicalPath(root, directory, { leaf: "directory" });
  assert(stat.isDirectory() && !stat.isSymbolicLink(), "Matched backup media must be a real directory.");
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    const relativePath = relative(root, path).replaceAll("\\", "/");
    assert(isContainedBy(root, path) && !entry.isSymbolicLink(), "Matched backup media contains an unsafe path.");
    if (entry.isDirectory()) files.push(...await mediaInventory(root, path, signal));
    else if (entry.isFile()) files.push({ path: relativePath, size: (await lstat(path)).size, sha256: await regularFileSha256(path, "media object", signal, root) });
    else throw new Error("Matched backup media contains an unsupported entry.");
  }
  return files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
}

function canonicalMediaPath(path) {
  assert(typeof path === "string" && path.length > 0 && path.length <= 1_024, "Source media inventory path is invalid.");
  const normalized = path.replaceAll("\\", "/").replace(/^\/+/, "");
  assert(!isAbsolute(normalized) && normalized.split("/").every((part) => part && part !== "." && part !== ".."), "Source media inventory path is unsafe.");
  return normalized;
}

function inventorySha256(inventory) {
  return createHash("sha256").update(JSON.stringify(inventory)).digest("hex");
}

function inventoriesEqual(left, right) {
  return left.length === right.length && left.every((item, index) => item.path === right[index].path && item.size === right[index].size && item.sha256 === right[index].sha256);
}

/** Captures source MinIO object paths, sizes, and bytes independently of the mirror destination. */
export async function captureSourceMediaInventory({ harness, signal, redact = [] }) {
  assert(harness && typeof harness.exec === "function", "A Docker source inventory harness is required.");
  const options = { ...(signal ? { signal } : {}), redact };
  const listed = await harness.exec("minio", ["mc", "ls", "--recursive", "--json", "fixture/cmsify-upgrade"], options);
  const records = listed.stdout.split(/\r?\n/).filter(Boolean);
  assert(records.length > 0 && records.length <= 10_000, "Source media inventory must be non-empty and bounded.");
  const inventory = [];
  for (let index = 0; index < records.length; index += 1) {
    let item;
    try {
      item = JSON.parse(records[index]);
    } catch {
      throw new Error("Source media inventory output is invalid.");
    }
    if (item.type !== undefined) assert(item.type === "file", "Source media inventory contains a non-file entry.");
    const path = canonicalMediaPath(item.key ?? item.name);
    assert(Number.isSafeInteger(item.size) && item.size >= 0, "Source media inventory size is invalid.");
    const temporary = `/tmp/cmsify-source-object-${createHash("sha256").update(path).digest("hex")}`;
    try {
      await harness.exec("minio", ["mc", "cp", `fixture/cmsify-upgrade/${path}`, temporary], options);
      const checksum = await harness.exec("minio", ["sha256sum", temporary], options);
      const sha256 = checksum.stdout.trim().split(/\s+/, 1)[0];
      assert(/^[0-9a-f]{64}$/.test(sha256), "Source media checksum output is invalid.");
      inventory.push({ path, size: item.size, sha256 });
    } finally {
      await harness.exec("minio", ["rm", "-f", temporary], options).catch(() => undefined);
    }
  }
  inventory.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  assert(new Set(inventory.map(({ path }) => path)).size === inventory.length, "Source media inventory contains duplicate paths.");
  return Object.freeze(inventory.map((item) => Object.freeze(item)));
}

function backupDirectoryFor(scope) {
  assertTrustedRunScope(scope);
  const backupDirectory = resolve(scope.diagnosticsDirectory, "backup");
  assert(isContainedBy(scope.diagnosticsDirectory, backupDirectory), "Matched backup path escapes the run-owned diagnostics directory.");
  return backupDirectory;
}

/** Creates and immediately verifies one checksum-bound PostgreSQL/media backup generation. */
export async function createMatchedBackup({ harness, scope, baselineVersion, now = () => new Date().toISOString(), signal, redact = [] }) {
  assert(harness && typeof harness.exec === "function" && typeof harness.copyFrom === "function", "A Docker backup harness is required.");
  assert(typeof baselineVersion === "string" && baselineVersion.length > 0, "Matched backup baseline version is required.");
  const backupDirectory = backupDirectoryFor(scope);
  const databasePath = resolve(backupDirectory, "database.dump");
  const mediaDirectory = resolve(backupDirectory, "media");
  const options = { ...(signal ? { signal } : {}), redact };
  throwIfCancelled(signal);
  await ensureSafeDirectory(scope.repositoryRoot, scope.diagnosticsDirectory);
  await assertPhysicalPath(scope.repositoryRoot, backupDirectory, { allowMissing: true });
  await mkdir(backupDirectory);
  await assertPhysicalPath(scope.repositoryRoot, backupDirectory, { leaf: "directory" });
  await mkdir(mediaDirectory);
  await assertPhysicalPath(scope.repositoryRoot, mediaDirectory, { leaf: "directory" });

  const sourceMedia = await captureSourceMediaInventory({ harness, signal, redact });

  await harness.exec("postgres", [
    "pg_dump", "--username", "cmsify", "--dbname", "cmsify", "--format=custom",
    "--no-owner", "--no-privileges", "--file=/tmp/cmsify-matched-backup.dump",
  ], options);
  await harness.copyFrom("postgres", "/tmp/cmsify-matched-backup.dump", databasePath, options);
  await harness.exec("minio", ["mc", "mirror", "--overwrite", "fixture/cmsify-upgrade", "/tmp/cmsify-matched-backup-media"], options);
  await harness.copyFrom("minio", "/tmp/cmsify-matched-backup-media/.", mediaDirectory, options);

  const suppliedCreatedAt = now();
  assert(typeof suppliedCreatedAt === "string" && suppliedCreatedAt.length <= 64 && Number.isFinite(Date.parse(suppliedCreatedAt)), "Matched backup creation time must be a timestamp.");
  const createdAt = new Date(suppliedCreatedAt).toISOString();
  const mirroredMedia = await mediaInventory(mediaDirectory, mediaDirectory, signal);
  assert(inventoriesEqual(sourceMedia, mirroredMedia), "Matched backup media inventory does not equal the independently observed source inventory.");
  const manifest = {
    schemaVersion: 1,
    runId: scope.runId,
    baselineVersion,
    createdAt,
    databaseSha256: await regularFileSha256(databasePath, "database dump", signal, backupDirectory),
    sourceMediaObjectCount: sourceMedia.length,
    sourceMediaInventorySha256: inventorySha256(sourceMedia),
    mediaObjects: sourceMedia,
  };
  assert(manifest.mediaObjects.length > 0, "Matched backup must contain media objects.");
  const manifestPath = resolve(backupDirectory, "backup-manifest.json");
  const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;
  await writeSafeAtomically(scope.repositoryRoot, manifestPath, manifestText, { encoding: "utf8", mode: 0o600 });
  const manifestSha256 = createHash("sha256").update(manifestText).digest("hex");
  await verifyMatchedBackup({ scope, baselineVersion, manifestSha256, signal, sourceMediaInventory: sourceMedia });
  return Object.freeze({ backupDirectory, manifestSha256, manifest: Object.freeze(manifest) });
}

/** Re-reads and verifies the exact matched backup generation. */
export async function verifyMatchedBackup({ harness, scope, baselineVersion, manifestSha256, signal, redact = [], sourceMediaInventory }) {
  assert(typeof baselineVersion === "string" && baselineVersion.length > 0, "Matched backup baseline version is required.");
  assert(typeof manifestSha256 === "string" && /^[0-9a-f]{64}$/.test(manifestSha256), "Matched backup manifest SHA-256 is required.");
  const backupDirectory = backupDirectoryFor(scope);
  throwIfCancelled(signal);
  // Docker/source inventory is the externally injectable hook. Observe it first so the
  // manifest and every local member are checked only after that hook has fully settled.
  const observedSource = sourceMediaInventory ?? (harness ? await captureSourceMediaInventory({ harness, signal, redact }) : undefined);
  const manifestPath = resolve(backupDirectory, "backup-manifest.json");
  let manifestText;
  try {
    manifestText = await readSafeFile(scope.repositoryRoot, manifestPath, "utf8");
  } catch {
    throw new Error("Matched backup manifest is missing.");
  }
  assert(createHash("sha256").update(manifestText).digest("hex") === manifestSha256, "Matched backup manifest changed after creation.");
  let manifest;
  try {
    manifest = JSON.parse(manifestText);
  } catch {
    throw new Error("Matched backup manifest is not valid JSON.");
  }
  assert(manifest && typeof manifest === "object" && !Array.isArray(manifest), "Matched backup manifest must be an object.");
  assert(Object.keys(manifest).sort().join(",") === ["baselineVersion", "createdAt", "databaseSha256", "mediaObjects", "runId", "schemaVersion", "sourceMediaInventorySha256", "sourceMediaObjectCount"].sort().join(","), "Matched backup manifest has unknown or missing fields.");
  assert(manifest.schemaVersion === 1, "Matched backup manifest schema is unsupported.");
  assert(manifest.runId === scope.runId, "Matched backup run ID does not match the current rehearsal.");
  assert(manifest.baselineVersion === baselineVersion, "Matched backup baseline does not match the current rehearsal.");
  assert(typeof manifest.createdAt === "string" && Number.isFinite(Date.parse(manifest.createdAt)), "Matched backup creation time is invalid.");
  assert(typeof manifest.databaseSha256 === "string" && /^[0-9a-f]{64}$/.test(manifest.databaseSha256), "Matched backup database checksum is invalid.");
  assert(await regularFileSha256(resolve(backupDirectory, "database.dump"), "database dump", signal, backupDirectory) === manifest.databaseSha256, "Matched backup database checksum mismatch.");
  assert(Array.isArray(manifest.mediaObjects) && manifest.mediaObjects.length > 0, "Matched backup media manifest must be non-empty.");
  assert(manifest.sourceMediaObjectCount === manifest.mediaObjects.length, "Matched backup source media count is invalid.");
  assert(typeof manifest.sourceMediaInventorySha256 === "string" && /^[0-9a-f]{64}$/.test(manifest.sourceMediaInventorySha256), "Matched backup source inventory checksum is invalid.");
  const actualMedia = await mediaInventory(resolve(backupDirectory, "media"), resolve(backupDirectory, "media"), signal);
  const declaredPaths = new Set();
  for (const item of manifest.mediaObjects) {
    assert(item && typeof item === "object" && !Array.isArray(item) && Object.keys(item).sort().join(",") === "path,sha256,size", "Matched backup media entry is invalid.");
    assert(typeof item.path === "string" && item.path.length > 0 && !item.path.includes("\\") && !isAbsolute(item.path) && item.path.split("/").every((part) => part && part !== "." && part !== ".."), "Matched backup media path is unsafe.");
    assert(Number.isSafeInteger(item.size) && item.size >= 0 && typeof item.sha256 === "string" && /^[0-9a-f]{64}$/.test(item.sha256) && !declaredPaths.has(item.path), "Matched backup media checksum entry is invalid.");
    declaredPaths.add(item.path);
  }
  assert(actualMedia.length === manifest.mediaObjects.length && actualMedia.every((item, index) => item.path === manifest.mediaObjects[index].path && item.size === manifest.mediaObjects[index].size), "Matched backup media inventory mismatch.");
  for (let index = 0; index < actualMedia.length; index += 1) {
    assert(actualMedia[index].sha256 === manifest.mediaObjects[index].sha256, "Matched backup media checksum mismatch.");
  }
  assert(inventorySha256(manifest.mediaObjects) === manifest.sourceMediaInventorySha256, "Matched backup source inventory fence mismatch.");
  if (observedSource !== undefined) assert(inventoriesEqual(observedSource, manifest.mediaObjects), "Matched backup source inventory changed or is incomplete.");
  return Object.freeze({ backupDirectory, manifestSha256, manifest: Object.freeze(manifest) });
}

const SAFE_ASSERTION_NAME = /^[a-z0-9][a-z0-9-]{0,63}$/;
const SAFE_CANARY_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-57][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function failureSummary(error, phaseName, kind) {
  const raw = messageOf(error);
  if (kind === "diagnostics") return { code: "diagnostic-capture-failed", message: "Diagnostic capture failed; diagnostic detail withheld." };
  if (kind === "cleanup") return { code: "cleanup-failed", message: "Owned-resource cleanup failed; diagnostic detail withheld." };
  if (error instanceof ReportPersistenceFailure) return { code: "report-persistence-failed", message: `Report persistence failed during the ${error.phaseName} phase.` };
  if (/cancel(?:led|ation)|aborted/i.test(raw)) return { code: "cancelled", message: "Upgrade rehearsal was cancelled." };
  if (/timed? out|did not become ready/i.test(raw)) return { code: "timeout", message: `The ${phaseName} phase timed out.` };
  if (/build metadata/i.test(raw)) return { code: "candidate-build-metadata", message: "Candidate version build metadata is not accepted." };
  if (/SemVer/i.test(raw)) return { code: "candidate-semver", message: "Candidate version must be valid SemVer." };
  if (/candidate image reference is malformed/i.test(raw)) return { code: "candidate-reference", message: "Candidate image reference is malformed." };
  if (/candidate source SHA/i.test(raw)) return { code: "candidate-source", message: "Candidate source SHA is invalid." };
  if (/Docker prerequisite/i.test(raw)) return { code: "docker-prerequisite", message: "Docker prerequisite check failed." };
  if (/matched backup|backup manifest|backup media|source media/i.test(raw)) return { code: "backup-validation", message: "Matched backup validation failed." };
  if (/invariant failed|^Invariant [a-z0-9-]+ failed:/i.test(raw)) return { code: "invariant-failed", message: `The ${phaseName} phase invariant failed.` };
  return { code: "phase-failed", message: `The ${phaseName} phase failed; diagnostic detail withheld.` };
}

function safeError(error, phaseName, kind) {
  const summary = failureSummary(error, phaseName, kind);
  const result = new Error(summary.message.slice(0, 256));
  result.name = "RehearsalDiagnostic";
  result.code = summary.code;
  result.phase = phaseName;
  return result;
}

function safeEvidence(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const evidence = {};
  if (Array.isArray(value.readiness)) {
    evidence.readiness = value.readiness.slice(0, 16).map((item) => ({
      service: typeof item?.service === "string" && /^[a-zA-Z0-9 ._-]{1,64}$/.test(item.service) ? item.service : "service",
      status: item?.status === "ready" ? "ready" : "failed",
      attempts: Number.isSafeInteger(item?.attempts) && item.attempts > 0 ? Math.min(item.attempts, 10_000) : 1,
    }));
  }
  if (Array.isArray(value.assertions)) {
    evidence.assertions = value.assertions.slice(0, 128).map((item) => ({
      name: typeof item?.name === "string" && SAFE_ASSERTION_NAME.test(item.name) ? item.name : "assertion",
      status: item?.status === "passed" ? "passed" : "failed",
    }));
  }
  if (typeof value.manifestSha256 === "string" && /^[0-9a-f]{64}$/.test(value.manifestSha256)) evidence.manifestSha256 = value.manifestSha256;
  const mediaCount = value.manifest?.sourceMediaObjectCount ?? value.sourceMediaObjectCount;
  if (Number.isSafeInteger(mediaCount) && mediaCount >= 0) evidence.sourceMediaObjectCount = Math.min(mediaCount, 10_000);
  if (value.prerequisites?.status === "passed") evidence.prerequisites = { status: "passed", mode: "immutable-image-nonpersistent-probes" };
  if (value.backupVerified === true) evidence.backupVerified = true;
  if (value.dataVolumesDiscarded === true) evidence.dataVolumesDiscarded = true;
  if (typeof value.canaryId === "string" && SAFE_CANARY_ID.test(value.canaryId)) evidence.canaryId = value.canaryId;
  return Object.keys(evidence).length === 0 ? undefined : evidence;
}

class ReportPersistenceFailure extends Error {
  constructor(phaseName, status) {
    super(`Report persistence failed during the ${phaseName} ${status} boundary.`);
    this.name = "ReportPersistenceFailure";
    this.phaseName = phaseName;
    this.status = status;
  }
}

class CleanupBoundaryFailure extends AggregateError {
  constructor(records) {
    super(records.map(({ error }) => error), "Cleanup boundary failed.", { cause: records[0].error });
    this.name = "CleanupBoundaryFailure";
    this.records = records;
  }
}

export class RehearsalFailure extends Error {
  constructor(message, { cause, reportPath, phase }) {
    super(message, { cause });
    this.name = "RehearsalFailure";
    this.reportPath = reportPath;
    this.phase = phase;
  }
}

async function writeReportAtomically(path, report) {
  await writeSafeAtomically(report.repositoryRoot, path, `${JSON.stringify(report.value, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
}

function createReport(scope, options, now) {
  return {
    schemaVersion: 1,
    runId: scope.runId,
    status: "running",
    result: "failed",
    startedAt: now(),
    completedAt: null,
    fixtureDigest: null,
    baselineImage: null,
    candidate: {
      reference: null,
      version: null,
      sourceSha: null,
    },
    canaryId: null,
    diagnostics: null,
    phases: REHEARSAL_PHASES.map((name) => ({
      name,
      status: "pending",
      startedAt: null,
      completedAt: null,
    })),
  };
}

function operationFor(operations, phase) {
  const names = {
    preflight: "preflight",
    "restore-fixture": "restoreFixture",
    baseline: "baseline",
    backup: "backup",
    upgrade: "upgrade",
    candidate: "candidate",
    "backup-reverify": "backupReverify",
    "discard-upgraded-state": "discardUpgradedState",
    "restore-backup": "restoreBackup",
    rollback: "rollback",
    cleanup: "cleanup",
  };
  return operations[names[phase]];
}

/**
 * Runs the ordered moving-baseline upgrade and rollback rehearsal.
 * @param {object} options
 * @returns {Promise<object>}
 */
export async function rehearse(options) {
  const scope = createRunScope(options.repositoryRoot, options.runId);
  const reportPath = resolve(scope.diagnosticsDirectory, "report.json");
  const clock = options.now ?? (() => new Date().toISOString());
  const now = () => {
    const value = clock();
    assert(typeof value === "string" && value.length <= 64 && Number.isFinite(Date.parse(value)), "Rehearsal clock must return a timestamp.");
    return new Date(value).toISOString();
  };
  const report = createReport(scope, options, now);
  const persist = options.reportWriter ?? ((value) => writeReportAtomically(reportPath, { repositoryRoot: scope.repositoryRoot, value }));
  const context = { ...options, now, scope, report, reportPath };
  const operations = options.operations ?? createDefaultOperations(context, options.dependencies);
  let primaryFailure;
  let primaryPhase;
  let primaryKind;
  const secondaryFailures = [];

  const commit = async (phaseName, status, mutate) => {
    const next = structuredClone(report);
    mutate(next);
    try {
      await persist(structuredClone(next));
    } catch {
      throw new ReportPersistenceFailure(phaseName, status);
    }
    for (const key of Object.keys(report)) delete report[key];
    Object.assign(report, next);
  };

  const transition = async (phaseName, status, error, evidence, reportMutation, failureKind) => {
    const phaseIndex = REHEARSAL_PHASES.indexOf(phaseName);
    await commit(phaseName, status, (next) => {
      const phase = next.phases[phaseIndex];
      if (status === "running") {
        if (phase.status !== "pending") throw new Error(`Rehearsal phase ${phaseName} cannot be re-entered.`);
        if (phaseName !== "cleanup") {
          const earlier = next.phases.slice(0, phaseIndex);
          if (earlier.some((entry) => entry.status !== "passed")) throw new Error(`Rehearsal phase ${phaseName} cannot skip an earlier phase.`);
        }
        phase.status = "running";
        phase.startedAt = now();
      } else {
        if (phase.status !== "running" || !["passed", "failed"].includes(status)) throw new Error(`Rehearsal phase ${phaseName} has an invalid transition.`);
        phase.status = status;
        phase.completedAt = now();
        const allowedEvidence = safeEvidence(evidence);
        if (allowedEvidence) phase.evidence = allowedEvidence;
        if (error !== undefined) {
          const summary = failureSummary(error, phaseName, failureKind);
          phase.error = summary.message.slice(0, 256);
          phase.errorCode = summary.code;
        }
      }
      if (reportMutation) reportMutation(next);
    });
  };

  const terminalizeFailedPhase = async (phaseName, error, failureKind) => {
    const phaseIndex = REHEARSAL_PHASES.indexOf(phaseName);
    await commit(phaseName, "failed", (next) => {
      const phase = next.phases[phaseIndex];
      if (phase.status === "pending") phase.startedAt = now();
      if (phase.status !== "passed") {
        phase.status = "failed";
        phase.completedAt = now();
        const summary = failureSummary(error, phaseName, failureKind);
        phase.error = summary.message.slice(0, 256);
        phase.errorCode = summary.code;
        const allowedEvidence = safeEvidence(error?.safeEvidence);
        if (allowedEvidence) phase.evidence = allowedEvidence;
      }
      next.status = "failed";
      if (phaseName === "cleanup") next.completedAt = now();
    });
  };

  const runPhase = async (phaseName) => {
    const operation = operationFor(operations, phaseName);
    await transition(phaseName, "running");
    let value;
    try {
      if (phaseName === "preflight") validateCandidateInput(context);
      value = await operation(context);
      if (phaseName === "backup" && value) context.backup = value;
      if (phaseName === "candidate" && value?.canaryId) {
        assert(SAFE_CANARY_ID.test(value.canaryId), "Candidate assertions returned a malformed canary ID.");
        context.canaryId = value.canaryId;
      }
    } catch (error) {
      try {
        await transition(phaseName, "failed", error, error?.safeEvidence);
      } catch (reportFailure) {
        const combined = new AggregateError([error, reportFailure], "Rehearsal operation and report persistence failed.", { cause: error });
        combined.phaseName = phaseName;
        combined.primaryError = error;
        combined.secondaryErrors = [reportFailure];
        throw combined;
      }
      if (error && typeof error === "object") {
        error.phaseName = phaseName;
        throw error;
      }
      const wrapped = new Error("Rehearsal operation failed with a non-error value.");
      wrapped.phaseName = phaseName;
      throw wrapped;
    }
    await transition(phaseName, "passed", undefined, value, (next) => {
      if (phaseName === "preflight" && value) {
        assert(typeof value.fixtureDigest === "string" && /^[0-9a-f]{64}$/.test(value.fixtureDigest), "Preflight did not return the verified fixture digest.");
        assert(value.baselineImage && typeof value.baselineImage === "object"
          && typeof value.baselineImage.repository === "string" && value.baselineImage.repository.length > 0
          && typeof value.baselineImage.tag === "string" && value.baselineImage.tag.length > 0
          && typeof value.baselineImage.digest === "string" && /^sha256:[0-9a-f]{64}$/.test(value.baselineImage.digest)
          && value.baselineImage.platform === "linux/amd64", "Preflight did not return the exact baseline image identity.");
        next.fixtureDigest = value.fixtureDigest;
        next.baselineImage = structuredClone(value.baselineImage);
        next.candidate = {
          reference: options.candidateImage,
          version: value.version ?? options.candidateVersion,
          sourceSha: value.sourceSha ?? options.candidateSourceSha,
          imageId: typeof value.imageId === "string" && /^sha256:[0-9a-f]{64}$/.test(value.imageId) ? value.imageId : null,
          platform: value.platform === "linux/amd64" ? value.platform : null,
          informationalVersion: value.informationalVersion === `${options.candidateVersion}+${options.candidateSourceSha}` ? value.informationalVersion : null,
        };
        if (context.fixtureIdentity) next.fixture = structuredClone(context.fixtureIdentity);
      }
      if (phaseName === "candidate" && value?.canaryId) next.canaryId = value.canaryId;
    });
    return value;
  };

  const runCleanup = async () => {
    const failures = [];
    let startFailure;
    let cleanupFailure;
    try {
      await transition("cleanup", "running");
    } catch (error) {
      startFailure = error;
      failures.push({ error });
    }
    try {
      await operations.cleanup(context);
    } catch (error) {
      cleanupFailure = error;
      failures.push({ error, kind: "cleanup" });
    }
    try {
      const cleanupPhase = report.phases.at(-1);
      if (cleanupPhase.status === "running") {
        await transition("cleanup", cleanupFailure === undefined ? "passed" : "failed", cleanupFailure, undefined, (next) => {
          next.status = primaryFailure === undefined && startFailure === undefined && cleanupFailure === undefined ? "passed" : "failed";
          next.result = next.status;
          next.completedAt = now();
        }, cleanupFailure === undefined ? undefined : "cleanup");
      } else if (cleanupPhase.status === "pending") {
        await commit("cleanup", "failed", (next) => {
          const phase = next.phases.at(-1);
          phase.status = "failed";
          phase.startedAt = now();
          phase.completedAt = now();
          const summary = failureSummary(startFailure ?? cleanupFailure, "cleanup", startFailure === undefined ? "cleanup" : undefined);
          phase.error = summary.message;
          phase.errorCode = summary.code;
          next.status = "failed";
          next.result = "failed";
          next.completedAt = now();
        });
      }
    } catch (error) {
      failures.push({ error });
    }
    if (failures.length > 0) throw new CleanupBoundaryFailure(failures);
  };

  try {
    for (const phase of REHEARSAL_PHASES.slice(0, -1)) await runPhase(phase);
  } catch (error) {
    primaryFailure = error.primaryError ?? error;
    primaryPhase = error.phaseName ?? primaryFailure.phaseName ?? "preflight";
    if (Array.isArray(error.secondaryErrors)) secondaryFailures.push(...error.secondaryErrors.map((item) => ({ error: item, phase: primaryPhase })));
    try {
      await terminalizeFailedPhase(primaryPhase, primaryFailure);
    } catch (reportFailure) {
      secondaryFailures.push({ error: reportFailure, phase: primaryPhase });
    }
    let diagnosticFailure;
    let diagnosticResult;
    try {
      diagnosticResult = await operations.captureDiagnostics(context);
    } catch (error) {
      diagnosticFailure = error;
    }
    if (diagnosticFailure !== undefined) {
      secondaryFailures.push({ error: diagnosticFailure, phase: primaryPhase, kind: "diagnostics" });
      try {
        await commit(primaryPhase, "diagnostics", (next) => { next.diagnostics = { status: "failed", code: "diagnostic-capture-failed" }; });
      } catch (reportFailure) {
        secondaryFailures.push({ error: reportFailure, phase: primaryPhase });
      }
    } else {
      try {
        await commit(primaryPhase, "diagnostics", (next) => {
          next.diagnostics = diagnosticResult?.status === "unavailable"
            ? { status: "unavailable", code: "resources-not-started" }
            : { status: "captured" };
        });
      } catch (reportFailure) {
        secondaryFailures.push({ error: reportFailure, phase: primaryPhase });
      }
    }
  } finally {
    try {
      await runCleanup();
    } catch (cleanupFailure) {
      const records = cleanupFailure instanceof CleanupBoundaryFailure
        ? cleanupFailure.records
        : [{ error: cleanupFailure, kind: "cleanup" }];
      const boundary = records[0];
      if (report.phases.at(-1).status === "running") {
        try {
          await terminalizeFailedPhase("cleanup", boundary.error, boundary.kind);
        } catch (reportFailure) {
          secondaryFailures.push({ error: reportFailure, phase: "cleanup" });
        }
      }
      if (primaryFailure === undefined) {
        primaryFailure = boundary.error;
        primaryKind = boundary.kind;
        primaryPhase = "cleanup";
        secondaryFailures.push(...records.slice(1).map(({ error, kind }) => ({ error, phase: "cleanup", kind })));
      } else {
        secondaryFailures.push(...records.map(({ error, kind }) => ({ error, phase: "cleanup", kind })));
      }
    }
  }

  if (primaryFailure !== undefined) {
    const primarySafeError = safeError(primaryFailure, primaryPhase, primaryKind);
    const causes = [primarySafeError, ...secondaryFailures.map(({ error, phase, kind }) => safeError(error, phase, kind))];
    const cause = causes.length === 1 ? causes[0] : new AggregateError(causes, primarySafeError.message, { cause: primarySafeError });
    throw new RehearsalFailure(primarySafeError.message, {
      cause,
      reportPath: relative(scope.repositoryRoot, reportPath).replaceAll("\\", "/"),
      phase: primaryPhase,
    });
  }
  assert(report.status === "passed" && report.result === "passed" && report.phases.every(({ status }) => status === "passed"), "Rehearsal cannot pass unless every mandatory phase and cleanup passed.");
  return report;
}
