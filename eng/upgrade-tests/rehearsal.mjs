import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { lstat, mkdir, readdir, readFile, rename, writeFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

import { assertBaseline, assertCandidate, assertRollback, captureWebhookWorkerState } from "./assertions.mjs";
import { verifyFixtureChecksums } from "./checksums.mjs";
import { createDockerHarness } from "./docker.mjs";
import { loadExpectedData } from "./expected.mjs";
import { createDockerHttpAdapter } from "./http.mjs";
import { loadFixtureManifest } from "./manifest.mjs";
import { assertTrustedRunScope, createRunScope } from "./paths.mjs";

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
const CANDIDATE_SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?$/;
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
  assert(typeof candidateImage === "string" && candidateImage.length > 0 && candidateImage.length <= 512 && !/[\s\r\n\0]/.test(candidateImage) && !candidateImage.startsWith("-"), "Candidate image reference is malformed.");
  assert(typeof candidateVersion === "string" && CANDIDATE_SEMVER.test(candidateVersion), "Candidate version must be valid SemVer.");
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

function throwIfCancelled(signal) {
  if (signal?.aborted) throw new Error("Upgrade rehearsal was cancelled.");
}

async function waitUntilReady(check, description, { signal, timeoutMs = READINESS_TIMEOUT_MS } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    throwIfCancelled(signal);
    try {
      await check();
      return;
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
  throw new Error(`${description} did not become ready within ${timeoutMs} milliseconds.`, { cause: lastError });
}

function dockerOptions(context) {
  return {
    ...(context.signal ? { signal: context.signal } : {}),
    redact: context.redactions ?? [],
  };
}

async function waitForInfrastructure(context) {
  const options = dockerOptions(context);
  await waitUntilReady(
    () => context.harness.exec("postgres", ["pg_isready", "--username", "cmsify", "--dbname", "cmsify"], options),
    "PostgreSQL",
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
  await waitUntilReady(
    () => context.harness.exec("minio", ["curl", "--silent", "--show-error", "--fail", "http://localhost:9000/minio/health/live"], options),
    "MinIO",
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
}

async function waitForApi(context, service, description) {
  await waitUntilReady(
    () => context.harness.exec(service, ["curl", "--silent", "--show-error", "--fail", "http://localhost:8080/health/ready"], dockerOptions(context)),
    description,
    { signal: context.signal, timeoutMs: context.readinessTimeoutMs },
  );
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
  await waitForInfrastructure(context);
  await context.harness.copyTo("postgres", resolve(context.fixtureDirectory, "database.sql"), "/tmp/cmsify-upgrade-fixture.sql", dockerOptions(context));
  await context.harness.exec("postgres", [
    "psql", "--username", "cmsify", "--dbname", "cmsify", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--file=/tmp/cmsify-upgrade-fixture.sql",
  ], dockerOptions(context));
  await configureMedia(context);
  await context.harness.copyTo("minio", `${resolve(context.fixtureDirectory, "media")}${sep}.`, "/tmp/cmsify-upgrade-fixture-media", dockerOptions(context));
  await context.harness.exec("minio", ["mc", "mirror", "--overwrite", "/tmp/cmsify-upgrade-fixture-media", "fixture/cmsify-upgrade"], dockerOptions(context));
}

async function restoreBackupState(context) {
  throwIfCancelled(context.signal);
  await context.harness.up(["postgres", "minio"], dockerOptions(context));
  await waitForInfrastructure(context);
  const backupDirectory = backupDirectoryFor(context.scope);
  await context.harness.copyTo("postgres", resolve(backupDirectory, "database.dump"), "/tmp/cmsify-matched-restore.dump", dockerOptions(context));
  await context.harness.exec("postgres", [
    "pg_restore", "--username", "cmsify", "--dbname", "cmsify", "--clean", "--if-exists",
    "--no-owner", "--no-privileges", "--exit-on-error", "/tmp/cmsify-matched-restore.dump",
  ], dockerOptions(context));
  await configureMedia(context);
  await context.harness.copyTo("minio", `${resolve(backupDirectory, "media")}${sep}.`, "/tmp/cmsify-matched-restore-media", dockerOptions(context));
  await context.harness.exec("minio", ["mc", "mirror", "--overwrite", "/tmp/cmsify-matched-restore-media", "fixture/cmsify-upgrade"], dockerOptions(context));
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
      validateCandidateInput(context);
      context.manifest = loadManifest(context.fixtureDirectory);
      context.expected = await loadExpected(context.fixtureDirectory, context.manifest);
      await verifyChecksums(context.fixtureDirectory, context.manifest);
      await context.harness.inspectImage(context.manifest.baseline.apiImage, dockerOptions(context));
      await context.harness.inspectImage(context.manifest.baseline.postgresImage, dockerOptions(context));
      await context.harness.inspectImage(context.manifest.baseline.minioImage, dockerOptions(context));
      context.candidateIdentity = await context.harness.inspectCandidateImage(context.candidateImage, {
        version: context.candidateVersion,
        sourceSha: context.candidateSourceSha,
      }, dockerOptions(context));
      context.redactions = [
        ...Object.values(FIXTURE_ENVIRONMENT),
        context.expected.authentication.readerToken,
        context.expected.authentication.adminPassword,
      ];
      await context.harness.writeEnvironment({
        POSTGRES_IMAGE: imageReference(context.manifest.baseline.postgresImage),
        MINIO_IMAGE: imageReference(context.manifest.baseline.minioImage),
        BASELINE_API_IMAGE: imageReference(context.manifest.baseline.apiImage),
        CANDIDATE_API_IMAGE: context.candidateIdentity.imageId,
        CANDIDATE_API_IMAGE_REFERENCE: context.candidateIdentity.reference,
        CANDIDATE_API_IMAGE_ID: context.candidateIdentity.imageId,
        ...FIXTURE_ENVIRONMENT,
      });
      context.report.fixture = {
        baselineVersion: context.manifest.baseline.version,
        baselineSourceSha: context.manifest.baseline.sourceSha,
      };
      return context.candidateIdentity;
    },

    async restoreFixture() {
      await restoreFixtureState(context);
    },

    async baseline() {
      throwIfCancelled(context.signal);
      context.webhookWorkerStateBeforeStart = await captureWorkerState(assertionDocker(context), { ...context.expected.ids, ...context.expected.relatedIds });
      await context.harness.up(["baseline-api"], dockerOptions(context));
      await waitForApi(context, "baseline-api", "Published baseline API");
      return baselineAssertions(assertionContext(context, "baseline-api"));
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
      await waitForApi(context, "candidate-api", "Candidate API migration/startup");
    },

    async candidate() {
      const result = await candidateAssertions(assertionContext(context, "candidate-api", { candidate: context.candidateIdentity }));
      assert(typeof result?.canaryId === "string" && result.canaryId.length > 0, "Candidate assertions did not return the required canary ID.");
      return result;
    },

    async backupReverify() {
      throwIfCancelled(context.signal);
      await context.harness.stop("candidate-api", dockerOptions(context));
      return verifyBackup({
        scope: context.scope,
        baselineVersion: context.manifest.baseline.version,
        manifestSha256: context.backup.manifestSha256,
        signal: context.signal,
      });
    },

    async discardUpgradedState() {
      throwIfCancelled(context.signal);
      await context.harness.discardDataVolumes(dockerOptions(context));
    },

    async restoreBackup() {
      await restoreBackupState(context);
    },

    async rollback() {
      throwIfCancelled(context.signal);
      context.webhookWorkerStateBeforeStart = await captureWorkerState(assertionDocker(context), { ...context.expected.ids, ...context.expected.relatedIds });
      await context.harness.up(["baseline-api"], dockerOptions(context));
      await waitForApi(context, "baseline-api", "Rollback baseline API");
      return rollbackAssertions(assertionContext(context, "baseline-api", { canaryId: context.canaryId }));
    },

    async captureDiagnostics() {
      await context.harness.logs();
    },

    async cleanup() {
      await context.harness.cleanup();
    },
  };
}

async function regularFileSha256(path, description, signal) {
  throwIfCancelled(signal);
  let stat;
  try {
    stat = await lstat(path);
  } catch {
    throw new Error(`Matched backup is missing ${description}.`);
  }
  assert(stat.isFile() && !stat.isSymbolicLink(), `Matched backup ${description} must be a regular file.`);
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) {
    throwIfCancelled(signal);
    hash.update(chunk);
  }
  return hash.digest("hex");
}

async function mediaInventory(root, directory = root, signal) {
  throwIfCancelled(signal);
  let stat;
  try {
    stat = await lstat(directory);
  } catch {
    throw new Error("Matched backup media directory is missing.");
  }
  assert(stat.isDirectory() && !stat.isSymbolicLink(), "Matched backup media must be a real directory.");
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    const relativePath = relative(root, path).replaceAll("\\", "/");
    assert(isContainedBy(root, path) && !entry.isSymbolicLink(), `Matched backup media contains an unsafe path: ${relativePath}.`);
    if (entry.isDirectory()) files.push(...await mediaInventory(root, path, signal));
    else if (entry.isFile()) files.push({ path: relativePath, sha256: await regularFileSha256(path, `media object ${relativePath}`, signal) });
    else throw new Error(`Matched backup media contains an unsupported entry: ${relativePath}.`);
  }
  return files.sort((left, right) => left.path.localeCompare(right.path));
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
  await mkdir(scope.diagnosticsDirectory, { recursive: true });
  await mkdir(backupDirectory);
  await mkdir(mediaDirectory);

  await harness.exec("postgres", [
    "pg_dump", "--username", "cmsify", "--dbname", "cmsify", "--format=custom",
    "--no-owner", "--no-privileges", "--file=/tmp/cmsify-matched-backup.dump",
  ], options);
  await harness.copyFrom("postgres", "/tmp/cmsify-matched-backup.dump", databasePath, options);
  await harness.exec("minio", ["mc", "mirror", "--overwrite", "fixture/cmsify-upgrade", "/tmp/cmsify-matched-backup-media"], options);
  await harness.copyFrom("minio", "/tmp/cmsify-matched-backup-media/.", mediaDirectory, options);

  const createdAt = now();
  assert(typeof createdAt === "string" && Number.isFinite(Date.parse(createdAt)), "Matched backup creation time must be a timestamp.");
  const manifest = {
    schemaVersion: 1,
    runId: scope.runId,
    baselineVersion,
    createdAt,
    databaseSha256: await regularFileSha256(databasePath, "database dump", signal),
    mediaObjects: await mediaInventory(mediaDirectory, mediaDirectory, signal),
  };
  assert(manifest.mediaObjects.length > 0, "Matched backup must contain media objects.");
  const manifestPath = resolve(backupDirectory, "backup-manifest.json");
  const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;
  await writeFile(`${manifestPath}.tmp`, manifestText, { encoding: "utf8", mode: 0o600, flag: "wx" });
  await rename(`${manifestPath}.tmp`, manifestPath);
  const manifestSha256 = createHash("sha256").update(manifestText).digest("hex");
  await verifyMatchedBackup({ scope, baselineVersion, manifestSha256, signal });
  return Object.freeze({ backupDirectory, manifestSha256, manifest: Object.freeze(manifest) });
}

/** Re-reads and verifies the exact matched backup generation. */
export async function verifyMatchedBackup({ scope, baselineVersion, manifestSha256, signal }) {
  assert(typeof baselineVersion === "string" && baselineVersion.length > 0, "Matched backup baseline version is required.");
  assert(typeof manifestSha256 === "string" && /^[0-9a-f]{64}$/.test(manifestSha256), "Matched backup manifest SHA-256 is required.");
  const backupDirectory = backupDirectoryFor(scope);
  throwIfCancelled(signal);
  const manifestPath = resolve(backupDirectory, "backup-manifest.json");
  let manifestText;
  try {
    manifestText = await readFile(manifestPath, "utf8");
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
  assert(Object.keys(manifest).sort().join(",") === ["baselineVersion", "createdAt", "databaseSha256", "mediaObjects", "runId", "schemaVersion"].sort().join(","), "Matched backup manifest has unknown or missing fields.");
  assert(manifest.schemaVersion === 1, "Matched backup manifest schema is unsupported.");
  assert(manifest.runId === scope.runId, "Matched backup run ID does not match the current rehearsal.");
  assert(manifest.baselineVersion === baselineVersion, "Matched backup baseline does not match the current rehearsal.");
  assert(typeof manifest.createdAt === "string" && Number.isFinite(Date.parse(manifest.createdAt)), "Matched backup creation time is invalid.");
  assert(typeof manifest.databaseSha256 === "string" && /^[0-9a-f]{64}$/.test(manifest.databaseSha256), "Matched backup database checksum is invalid.");
  assert(await regularFileSha256(resolve(backupDirectory, "database.dump"), "database dump", signal) === manifest.databaseSha256, "Matched backup database checksum mismatch.");
  assert(Array.isArray(manifest.mediaObjects) && manifest.mediaObjects.length > 0, "Matched backup media manifest must be non-empty.");
  const actualMedia = await mediaInventory(resolve(backupDirectory, "media"), resolve(backupDirectory, "media"), signal);
  const declaredPaths = new Set();
  for (const item of manifest.mediaObjects) {
    assert(item && typeof item === "object" && !Array.isArray(item) && Object.keys(item).sort().join(",") === "path,sha256", "Matched backup media entry is invalid.");
    assert(typeof item.path === "string" && item.path.length > 0 && !item.path.includes("\\") && !isAbsolute(item.path) && item.path.split("/").every((part) => part && part !== "." && part !== ".."), "Matched backup media path is unsafe.");
    assert(typeof item.sha256 === "string" && /^[0-9a-f]{64}$/.test(item.sha256) && !declaredPaths.has(item.path), "Matched backup media checksum entry is invalid.");
    declaredPaths.add(item.path);
  }
  assert(actualMedia.length === manifest.mediaObjects.length && actualMedia.every((item, index) => item.path === manifest.mediaObjects[index].path), "Matched backup media inventory mismatch.");
  for (let index = 0; index < actualMedia.length; index += 1) {
    assert(actualMedia[index].sha256 === manifest.mediaObjects[index].sha256, `Matched backup media checksum mismatch for ${actualMedia[index].path}.`);
  }
  return Object.freeze({ backupDirectory, manifestSha256, manifest: Object.freeze(manifest) });
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function sanitizedMessage(error, options) {
  let message = messageOf(error).replace(/[\r\n]+/g, " ").trim() || "Upgrade rehearsal failed.";
  const replacements = [
    [options.fixtureDirectory, "<fixture>"],
    [options.repositoryRoot, "<repository>"],
    ...((options.redact ?? []).map((value) => [value, "<redacted>"])),
  ];
  for (const [value, replacement] of replacements) {
    if (typeof value === "string" && value.length > 0) message = message.replace(new RegExp(escapeRegex(value), "gi"), replacement);
  }
  return message
    .replace(/cmsify_[a-z0-9._-]{12,}/gi, "<redacted>")
    .replace(/(password|secret|token|encryption[_-]?key)\s*[=:]\s*[^\s,;]+/gi, "$1=<redacted>");
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
  await mkdir(resolve(path, ".."), { recursive: true });
  const temporary = `${path}.tmp`;
  await writeFile(temporary, `${JSON.stringify(report, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
  await rename(temporary, path);
}

function createReport(scope, options, now) {
  return {
    schemaVersion: 1,
    runId: scope.runId,
    status: "running",
    startedAt: now(),
    completedAt: null,
    candidate: {
      reference: options.candidateImage,
      version: options.candidateVersion,
      sourceSha: options.candidateSourceSha,
    },
    canaryId: null,
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
  const now = options.now ?? (() => new Date().toISOString());
  const report = createReport(scope, options, now);
  const persist = options.reportWriter ?? ((value) => writeReportAtomically(reportPath, value));
  const context = { ...options, now, scope, report, reportPath };
  const operations = options.operations ?? createDefaultOperations(context, options.dependencies);
  let primaryFailure;

  const sanitize = (error) => sanitizedMessage(error, {
    ...options,
    redact: [...(options.redact ?? []), ...(context.redactions ?? [])],
  });

  const transition = async (phaseName, status, error) => {
    const phaseIndex = REHEARSAL_PHASES.indexOf(phaseName);
    const phase = report.phases[phaseIndex];
    if (status === "running") {
      if (phase.status !== "pending") throw new Error(`Rehearsal phase ${phaseName} cannot be re-entered.`);
      if (phaseName !== "cleanup") {
        const earlier = report.phases.slice(0, phaseIndex);
        if (earlier.some((entry) => entry.status !== "passed")) throw new Error(`Rehearsal phase ${phaseName} cannot skip an earlier phase.`);
      }
      phase.status = "running";
      phase.startedAt = now();
    } else {
      if (phase.status !== "running" || !["passed", "failed"].includes(status)) throw new Error(`Rehearsal phase ${phaseName} has an invalid transition.`);
      phase.status = status;
      phase.completedAt = now();
      if (error !== undefined) phase.error = sanitize(error);
    }
    await persist(report);
  };

  const runPhase = async (phaseName) => {
    const operation = operationFor(operations, phaseName);
    await transition(phaseName, "running");
    let value;
    try {
      value = await operation(context);
      if (phaseName === "preflight" && value) report.candidate = { ...report.candidate, ...value };
      if (phaseName === "backup" && value) context.backup = value;
      if (phaseName === "candidate" && value?.canaryId) {
        report.canaryId = value.canaryId;
        context.canaryId = value.canaryId;
      }
    } catch (error) {
      try {
        await transition(phaseName, "failed", error);
      } catch (reportFailure) {
        throw new AggregateError([error, reportFailure], messageOf(error), { cause: error });
      }
      throw error;
    }
    await transition(phaseName, "passed");
    return value;
  };

  const runCleanup = async () => {
    const failures = [];
    let cleanupFailure;
    try {
      await transition("cleanup", "running");
    } catch (error) {
      failures.push(error);
    }
    try {
      await operations.cleanup(context);
    } catch (error) {
      cleanupFailure = error;
      failures.push(error);
    }
    report.status = primaryFailure === undefined && failures.length === 0 ? "passed" : "failed";
    report.completedAt = now();
    const cleanupPhase = report.phases.at(-1);
    if (cleanupPhase.status === "running") {
      try {
        await transition("cleanup", cleanupFailure === undefined ? "passed" : "failed", cleanupFailure);
      } catch (error) {
        failures.push(error);
      }
    }
    if (failures.length === 1) throw failures[0];
    if (failures.length > 1) throw new AggregateError(failures, messageOf(failures[0]), { cause: failures[0] });
  };

  try {
    for (const phase of REHEARSAL_PHASES.slice(0, -1)) await runPhase(phase);
  } catch (error) {
    primaryFailure = error;
    report.status = "failed";
    try {
      await operations.captureDiagnostics(context);
    } catch {
      // Diagnostic capture is best effort and cannot mask the rehearsal failure.
    }
  } finally {
    try {
      await runCleanup();
    } catch (cleanupFailure) {
      report.status = "failed";
      if (primaryFailure === undefined) primaryFailure = cleanupFailure;
      else primaryFailure = new AggregateError([primaryFailure, cleanupFailure], messageOf(primaryFailure), { cause: primaryFailure });
    }
  }

  if (primaryFailure !== undefined) {
    const failedPhase = report.phases.find(({ status }) => status === "failed")?.name ?? "cleanup";
    throw new RehearsalFailure(sanitize(primaryFailure), {
      cause: primaryFailure,
      reportPath,
      phase: failedPhase,
    });
  }
  return report;
}
