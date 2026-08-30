import { lstat, mkdir, open, readlink, realpath, rename, rm } from "node:fs/promises";
import { dirname, join, parse, relative, resolve, sep } from "node:path";
import { createHash, randomBytes } from "node:crypto";

export const RELEASE_SMOKE_SCENARIOS = Object.freeze([
  "descriptor-label-identity",
  "postgresql-readiness",
  "api-live-ready",
  "admin-static-assets",
  "local-login",
  "workspace-api-client-auth",
  "template-content-crud-etag",
  "media-upload-download",
  "oidc-api-admin-token-forwarding",
  "webhook-delivery",
  "scheduled-publication",
  "graceful-restart-persistence",
  "matched-backup",
  "destructive-canary",
  "fresh-restore",
  "restored-state-verification",
]);

const SHA256 = /^(?:sha256:)?[0-9a-f]{64}$/;
const SOURCE_SHA = /^[0-9a-f]{40}$/;
const RUN_ID = /^cmsify-smoke-[a-z0-9-]{8,32}$/;
const SEMVER = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const CREDENTIAL_PATTERNS = [
  /\b(?:cmsify|whsec)_[A-Za-z0-9._~-]+\b/g,
  /\bBearer\s+[A-Za-z0-9._~+\/-]+=*\b/gi,
  /\b(?:password|secret|token|authorization)\s*[:=]\s*[^\s,;]+/gi,
];
const evidenceTargetStates = new Map();
const MAX_EVIDENCE_ENTRY_BYTES = 128 * 1024;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function resolveEvidenceTarget(outputDirectory) {
  assert(typeof outputDirectory === "string" && outputDirectory.trim().length > 0 && !/[\0\r\n]/.test(outputDirectory), "Release smoke output directory is required.");
  const output = resolve(outputDirectory);
  assert(output !== parse(output).root, "Release smoke output directory cannot be a filesystem root.");
  const path = resolve(output, "evidence.json");
  assert(dirname(path) === output, "Release smoke evidence path escaped its output directory.");
  return { output, path };
}

function normalizedPathKey(path) {
  return process.platform === "win32" ? path.toLowerCase() : path;
}

function samePath(left, right) {
  return normalizedPathKey(resolve(left)) === normalizedPathKey(resolve(right));
}

function outputComponents(output) {
  const root = parse(output).root;
  const tail = relative(root, output);
  const result = [root];
  let current = root;
  for (const segment of tail.split(sep).filter(Boolean)) {
    current = join(current, segment);
    result.push(current);
  }
  return result;
}

function evidenceTargetError(code, message, cause) {
  const error = new Error(message, cause ? { cause } : undefined);
  error.name = "EvidenceTargetError";
  error.code = code;
  error.targetUnavailable = false;
  return error;
}

function assertRealDirectoryEntry(entry, stats) {
  if (stats.isSymbolicLink()) {
    throw evidenceTargetError(
      "evidence-output-indirection",
      `Release smoke output directory contains path indirection at ${entry}.`,
    );
  }
  if (!stats.isDirectory()) {
    throw evidenceTargetError(
      "evidence-output-unavailable",
      `Release smoke output path component is not a directory at ${entry}.`,
    );
  }
}

function entryIdentity(stats) {
  const kind = stats.isSymbolicLink()
    ? "symbolic-link"
    : stats.isDirectory()
      ? "directory"
      : stats.isFile()
        ? "file"
        : "other";
  return Object.freeze({
    dev: String(stats.dev),
    ino: String(stats.ino),
    kind,
  });
}

function sameEntryIdentity(left, right) {
  return left.dev === right.dev && left.ino === right.ino && left.kind === right.kind;
}

async function inspectPath(path, inspect) {
  try {
    return { absent: false, stats: await inspect(path, { bigint: true }) };
  } catch (error) {
    if (error?.code === "ENOENT") return { absent: true, stats: null };
    throw error;
  }
}

async function requireDirectoryIdentity(path, expectedIdentity, inspect, { invalidation = false } = {}) {
  let current;
  try {
    current = await inspectPath(path, inspect);
  } catch (error) {
    const makeError = invalidation ? evidenceInvalidationError : evidenceTargetError;
    throw makeError(
      "evidence-output-unverified",
      "Release smoke output directory identity could not be verified.",
      ...(invalidation ? [false, error] : [error]),
    );
  }
  const currentIdentity = current.absent ? null : entryIdentity(current.stats);
  if (
    current.absent
    || current.stats.isSymbolicLink()
    || !current.stats.isDirectory()
    || !sameEntryIdentity(currentIdentity, expectedIdentity)
  ) {
    const makeError = invalidation ? evidenceInvalidationError : evidenceTargetError;
    throw makeError(
      "evidence-output-identity-changed",
      "Release smoke output directory identity changed before evidence mutation.",
      ...(invalidation ? [false] : []),
    );
  }
  return currentIdentity;
}

async function validateCanonicalOutput(output, {
  create = false,
  inspect = lstat,
  canonicalize = realpath,
  makeDirectory = mkdir,
} = {}) {
  const components = outputComponents(output);
  let parentIdentity;
  for (const [index, entry] of components.entries()) {
    let stats;
    try {
      stats = await inspect(entry, { bigint: true });
    } catch (error) {
      if (error?.code !== "ENOENT" || !create || index === 0) {
        throw evidenceTargetError(
          error?.code === "ENOENT" ? "evidence-output-unavailable" : "evidence-output-unverified",
          error?.code === "ENOENT"
            ? "Release smoke output directory must already exist for evidence invalidation."
            : "Release smoke output directory could not be verified.",
          error,
        );
      }
      try {
        const parent = components[index - 1];
        await requireDirectoryIdentity(parent, parentIdentity, inspect);
        await makeDirectory(entry, { mode: 0o700 });
        await requireDirectoryIdentity(parent, parentIdentity, inspect);
        stats = await inspect(entry, { bigint: true });
      } catch (createError) {
        if (createError?.code === "evidence-output-identity-changed") throw createError;
        throw evidenceTargetError(
          "evidence-output-unavailable",
          "Release smoke output directory could not be created safely.",
          createError,
        );
      }
    }
    assertRealDirectoryEntry(entry, stats);
    parentIdentity = entryIdentity(stats);
  }

  let canonical;
  try {
    canonical = await canonicalize(output);
  } catch (error) {
    throw evidenceTargetError(
      "evidence-output-unverified",
      "Release smoke output directory canonical path could not be verified.",
      error,
    );
  }
  if (samePath(canonical, parse(canonical).root)) {
    throw evidenceTargetError(
      "evidence-output-root",
      "Release smoke output directory canonical target cannot be a filesystem root.",
    );
  }
  if (!samePath(canonical, output)) {
    throw evidenceTargetError(
      "evidence-output-indirection",
      "Release smoke output directory canonical path contains path indirection.",
    );
  }
  await requireDirectoryIdentity(output, parentIdentity, inspect);
  return Object.freeze({ path: canonical, identity: parentIdentity });
}

function targetState(path) {
  const key = normalizedPathKey(path);
  let state = evidenceTargetStates.get(key);
  if (!state) {
    state = { tail: Promise.resolve(), terminal: false, binding: null };
    evidenceTargetStates.set(key, state);
  }
  return state;
}

function enqueueTargetOperation(state, operation) {
  const predecessor = state.tail;
  let release;
  state.tail = new Promise((resolveSlot) => { release = resolveSlot; });
  return (async () => {
    await predecessor;
    try {
      return await operation();
    } finally {
      release();
    }
  })();
}

function evidenceInvalidationError(code, message, targetUnavailable, cause) {
  const error = new Error(message, cause ? { cause } : undefined);
  error.name = "EvidenceInvalidationError";
  error.code = code;
  error.targetUnavailable = targetUnavailable;
  return error;
}

async function inspectExactEntry(path, inspect) {
  return inspectPath(path, inspect);
}

async function hashFileHandle(handle) {
  const hash = createHash("sha256");
  const buffer = Buffer.allocUnsafe(8 * 1024);
  let offset = 0;
  while (offset <= MAX_EVIDENCE_ENTRY_BYTES) {
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, offset);
    if (bytesRead === 0) return hash.digest("hex");
    hash.update(buffer.subarray(0, bytesRead));
    offset += bytesRead;
  }
  throw evidenceTargetError(
    "evidence-entry-unverified",
    "Release smoke evidence entry exceeds the bounded identity check.",
  );
}

async function captureEvidenceEntry(path, {
  inspect = lstat,
  openFile = open,
  readLink = readlink,
} = {}) {
  let inspected;
  try {
    inspected = await inspectExactEntry(path, inspect);
  } catch (error) {
    throw evidenceTargetError(
      "evidence-entry-unverified",
      "Release smoke evidence entry identity could not be verified.",
      error,
    );
  }
  if (inspected.absent) return Object.freeze({ absent: true });

  const inspectedIdentity = entryIdentity(inspected.stats);
  if (inspected.stats.isSymbolicLink()) {
    let target;
    let confirmed;
    try {
      target = await readLink(path);
      confirmed = await inspectExactEntry(path, inspect);
    } catch (error) {
      throw evidenceTargetError(
        "evidence-entry-unverified",
        "Release smoke evidence link identity could not be verified.",
        error,
      );
    }
    if (
      confirmed.absent
      || !sameEntryIdentity(entryIdentity(confirmed.stats), inspectedIdentity)
    ) {
      throw evidenceTargetError(
        "evidence-entry-identity-changed",
        "Release smoke evidence entry identity changed during verification.",
      );
    }
    return Object.freeze({
      absent: false,
      identity: inspectedIdentity,
      digest: createHash("sha256").update(target, "utf8").digest("hex"),
    });
  }
  if (!inspected.stats.isFile()) {
    throw evidenceTargetError(
      "evidence-entry-unavailable",
      "Release smoke evidence entry must be a regular file or symbolic link.",
    );
  }

  let handle;
  try {
    handle = await openFile(path, "r");
    const handleStats = await handle.stat({ bigint: true });
    const handleIdentity = entryIdentity(handleStats);
    if (!sameEntryIdentity(handleIdentity, inspectedIdentity)) {
      throw evidenceTargetError(
        "evidence-entry-identity-changed",
        "Release smoke evidence entry identity changed while it was opened.",
      );
    }
    const digest = await hashFileHandle(handle);
    const confirmedHandleStats = await handle.stat({ bigint: true });
    const confirmed = await inspectExactEntry(path, inspect);
    if (
      confirmed.absent
      || !sameEntryIdentity(entryIdentity(confirmedHandleStats), handleIdentity)
      || !sameEntryIdentity(entryIdentity(confirmed.stats), handleIdentity)
    ) {
      throw evidenceTargetError(
        "evidence-entry-identity-changed",
        "Release smoke evidence entry identity changed during verification.",
      );
    }
    return Object.freeze({ absent: false, identity: handleIdentity, digest });
  } catch (error) {
    if (error?.name === "EvidenceTargetError") throw error;
    throw evidenceTargetError(
      "evidence-entry-unverified",
      "Release smoke evidence entry identity or content could not be verified.",
      error,
    );
  } finally {
    await handle?.close().catch(() => {});
  }
}

function sameEvidenceEntry(left, right) {
  return left.absent === right.absent
    && (left.absent || (
      sameEntryIdentity(left.identity, right.identity)
      && left.digest === right.digest
    ));
}

async function requireEvidenceEntry(path, expected, operations, {
  invalidation = false,
  code = "evidence-entry-identity-changed",
  message = "Release smoke evidence entry identity or content changed before mutation.",
} = {}) {
  let current;
  try {
    current = await captureEvidenceEntry(path, operations);
  } catch (error) {
    if (!invalidation) throw error;
    throw evidenceInvalidationError(code, message, false, error);
  }
  if (!sameEvidenceEntry(current, expected)) {
    if (invalidation) throw evidenceInvalidationError(code, message, false);
    throw evidenceTargetError(code, message);
  }
  return current;
}

function isoTimestamp(value, label) {
  assert(typeof value === "string" && Number.isFinite(Date.parse(value)), `${label} must be an ISO timestamp.`);
  return new Date(value).toISOString();
}

function candidate(value, label, allowUnknown) {
  assert(value && typeof value === "object" && !Array.isArray(value), `${label} candidate identity is required.`);
  assert(typeof value.reference === "string" && value.reference.length <= 256 && !/[\r\n\0]/.test(value.reference), `${label} candidate reference is invalid.`);
  assert((allowUnknown && value.imageId === null) || (typeof value.imageId === "string" && /^sha256:[0-9a-f]{64}$/.test(value.imageId)), `${label} candidate image ID is invalid.`);
  assert((allowUnknown && value.digest === null) || (typeof value.digest === "string" && /^sha256:[0-9a-f]{64}$/.test(value.digest)), `${label} candidate digest is invalid.`);
  return Object.freeze({ reference: value.reference, imageId: value.imageId, digest: value.digest });
}

function scenarios(value) {
  assert(Array.isArray(value) && value.length === RELEASE_SMOKE_SCENARIOS.length, "Release smoke evidence must contain every scenario exactly once.");
  return value.map((entry, index) => {
    assert(entry && typeof entry === "object" && entry.name === RELEASE_SMOKE_SCENARIOS[index], "Release smoke evidence scenario order is invalid.");
    assert(["pending", "passed", "failed"].includes(entry.status), `Release smoke scenario ${entry.name} has an invalid status.`);
    assert(Number.isSafeInteger(entry.durationMs) && entry.durationMs >= 0 && entry.durationMs <= 86_400_000, `Release smoke scenario ${entry.name} has an invalid duration.`);
    return Object.freeze({ name: entry.name, status: entry.status, durationMs: entry.durationMs });
  });
}

function backupHashes(value) {
  if (value === null || value === undefined) return null;
  assert(value && typeof value === "object" && !Array.isArray(value), "Release smoke backup hashes are invalid.");
  assert(typeof value.postgresSha256 === "string" && /^[0-9a-f]{64}$/.test(value.postgresSha256), "PostgreSQL backup hash is invalid.");
  assert(typeof value.mediaSha256 === "string" && /^[0-9a-f]{64}$/.test(value.mediaSha256), "Media backup hash is invalid.");
  return Object.freeze({ postgresSha256: value.postgresSha256, mediaSha256: value.mediaSha256 });
}

function sanitizeText(value, redactions) {
  let text = typeof value === "string" ? value : "Release smoke scenario failed.";
  for (const secret of redactions) {
    if (typeof secret === "string" && secret.length > 0) text = text.split(secret).join("<redacted>");
  }
  for (const pattern of CREDENTIAL_PATTERNS) text = text.replace(pattern, "<redacted>");
  return text.replace(/[\r\n\t]+/g, " ").replace(/\s+/g, " ").trim().slice(0, 512) || "Release smoke scenario failed.";
}

export function sanitizeFailure(error, { scenario, redactions = [] } = {}) {
  assert(typeof scenario === "string" && (RELEASE_SMOKE_SCENARIOS.includes(scenario) || scenario === "cleanup" || scenario === "signal"), "A valid failure scenario is required.");
  assert(Array.isArray(redactions) && redactions.every((value) => typeof value === "string"), "Failure redactions must be strings.");
  const rawCode = typeof error?.code === "string" ? error.code : "scenario-failed";
  const code = /^[a-z0-9-]{1,64}$/.test(rawCode) ? rawCode : "scenario-failed";
  return Object.freeze({
    scenario,
    code,
    message: sanitizeText(error?.message, redactions),
  });
}

export function createEvidence(input) {
  assert(input && typeof input === "object" && !Array.isArray(input), "Release smoke evidence input is required.");
  assert(typeof input.version === "string" && SEMVER.test(input.version), "Release smoke version must be valid SemVer without build metadata.");
  assert(typeof input.sourceSha === "string" && SOURCE_SHA.test(input.sourceSha), "Release smoke source SHA must be a full lowercase commit.");
  assert(typeof input.runId === "string" && RUN_ID.test(input.runId), "Release smoke run ID is invalid.");
  assert(["passed", "failed"].includes(input.status), "Release smoke status must be passed or failed.");
  assert(input.cleanup?.status === "passed" || input.cleanup?.status === "failed", "Release smoke cleanup status is required.");
  const failure = input.failure === null || input.failure === undefined
    ? null
    : sanitizeFailure(input.failure.error ?? input.failure, {
        scenario: input.failure.scenario,
        redactions: input.failure.redactions ?? [],
      });
  assert((input.status === "passed") === (failure === null), "Passed evidence cannot contain a failure and failed evidence must contain one.");

  const result = {
    schema: "cmsify.release-smoke.v1",
    schemaVersion: 1,
    runId: input.runId,
    status: input.status,
    version: input.version,
    sourceSha: input.sourceSha,
    startedAt: isoTimestamp(input.startedAt, "Release smoke start"),
    completedAt: isoTimestamp(input.completedAt, "Release smoke completion"),
    candidates: Object.freeze({
      api: candidate(input.candidates?.api, "API", input.status === "failed"),
      admin: candidate(input.candidates?.admin, "Admin", input.status === "failed"),
    }),
    scenarios: Object.freeze(scenarios(input.scenarios)),
    backupHashes: backupHashes(input.backupHashes),
    failure,
    cleanup: Object.freeze({ status: input.cleanup.status }),
  };
  assert(Date.parse(result.completedAt) >= Date.parse(result.startedAt), "Release smoke completion cannot precede its start.");
  assert(Buffer.byteLength(JSON.stringify(result), "utf8") <= 64 * 1024, "Release smoke evidence exceeds 64 KiB.");
  return Object.freeze(result);
}

export async function writeEvidence(outputDirectory, evidence, operations = {}) {
  const lexical = resolveEvidenceTarget(outputDirectory);
  const state = targetState(lexical.path);
  if (state.terminal) {
    throw evidenceTargetError(
      "evidence-operation-terminal",
      "Release smoke evidence target is permanently unavailable after invalidation began.",
    );
  }
  return enqueueTargetOperation(state, async () => {
    const inspect = operations.lstat ?? lstat;
    const openFile = operations.open ?? open;
    const readLink = operations.readlink ?? readlink;
    const makeDirectory = operations.mkdir ?? mkdir;
    const canonicalize = operations.realpath ?? realpath;
    const move = operations.rename ?? rename;
    const remove = operations.rm ?? rm;
    const lease = await validateCanonicalOutput(lexical.output, {
      create: true,
      inspect,
      canonicalize,
      makeDirectory,
    });
    const output = lease.path;
    const path = resolve(output, "evidence.json");
    assert(normalizedPathKey(path) === normalizedPathKey(lexical.path), "Release smoke evidence canonical path changed unexpectedly.");
    const temporary = resolve(output, `.evidence-${process.pid}-${randomBytes(6).toString("hex")}.tmp`);
    const entryOperations = { inspect, openFile, readLink };
    if (state.binding && !sameEntryIdentity(lease.identity, state.binding.outputIdentity)) {
      throw evidenceTargetError(
        "evidence-output-identity-changed",
        "Release smoke output directory no longer matches the directory where evidence was persisted.",
      );
    }
    const expectedDestination = state.binding?.evidence
      ?? await captureEvidenceEntry(path, entryOperations);
    await requireEvidenceEntry(path, expectedDestination, entryOperations);
    let handle;
    let temporaryEvidence;
    let renamed = false;
    try {
      await requireDirectoryIdentity(output, lease.identity, inspect);
      handle = await openFile(temporary, "wx", 0o600);
      await handle.writeFile(`${JSON.stringify(evidence, null, 2)}\n`, "utf8");
      await handle.sync();
      await handle.close();
      handle = undefined;
      temporaryEvidence = await captureEvidenceEntry(temporary, entryOperations);
      await requireDirectoryIdentity(output, lease.identity, inspect);
      await requireEvidenceEntry(temporary, temporaryEvidence, entryOperations);
      await requireEvidenceEntry(path, expectedDestination, entryOperations);
      await move(temporary, path);
      renamed = true;
      await requireDirectoryIdentity(output, lease.identity, inspect);
      const persistedEvidence = await requireEvidenceEntry(path, temporaryEvidence, entryOperations);
      state.binding = Object.freeze({
        outputIdentity: lease.identity,
        evidence: persistedEvidence,
      });
      return path;
    } finally {
      await handle?.close().catch(() => {});
      if (!renamed && temporaryEvidence) {
        try {
          await requireDirectoryIdentity(output, lease.identity, inspect);
          await requireEvidenceEntry(temporary, temporaryEvidence, entryOperations);
          await requireDirectoryIdentity(output, lease.identity, inspect);
          await remove(temporary, { force: true });
        } catch {
          // An unverified path is never cleaned through a changed output directory.
        }
      }
    }
  });
}

export async function invalidateEvidence(outputDirectory, operations = {}) {
  const lexical = resolveEvidenceTarget(outputDirectory);
  const state = targetState(lexical.path);
  if (state.terminal) {
    throw evidenceTargetError(
      "evidence-operation-terminal",
      "Release smoke evidence target is permanently unavailable after invalidation began.",
    );
  }
  state.terminal = true;
  return enqueueTargetOperation(state, async () => {
    const inspect = operations.lstat ?? lstat;
    const canonicalize = operations.realpath ?? realpath;
    const openFile = operations.open ?? open;
    const readLink = operations.readlink ?? readlink;
    const move = operations.rename ?? rename;
    const remove = operations.rm ?? rm;
    const entryOperations = { inspect, openFile, readLink };
    const lease = await validateCanonicalOutput(lexical.output, { inspect, canonicalize });
    const output = lease.path;
    const path = resolve(output, "evidence.json");
    assert(normalizedPathKey(path) === normalizedPathKey(lexical.path), "Release smoke evidence canonical path changed unexpectedly.");
    const quarantine = resolve(output, `.evidence-invalid-${process.pid}-${randomBytes(6).toString("hex")}.quarantine`);
    assert(dirname(quarantine) === output, "Release smoke evidence quarantine path escaped its output directory.");

    if (state.binding && !sameEntryIdentity(lease.identity, state.binding.outputIdentity)) {
      throw evidenceTargetError(
        "evidence-output-identity-changed",
        "Release smoke output directory no longer matches the directory where evidence was persisted.",
      );
    }
    const expectedEvidence = state.binding?.evidence
      ?? await captureEvidenceEntry(path, entryOperations);
    await requireDirectoryIdentity(output, lease.identity, inspect);
    await requireEvidenceEntry(path, expectedEvidence, entryOperations);
    if (expectedEvidence.absent) {
      return Object.freeze({ targetUnavailable: true, quarantineRemoved: true });
    }

    let moveError;
    let removalError;
    let cleanupError;
    let quarantined = false;
    try {
      await move(path, quarantine);
      quarantined = true;
    } catch (error) {
      moveError = error;
      try {
        await requireDirectoryIdentity(output, lease.identity, inspect, { invalidation: true });
        await requireEvidenceEntry(path, expectedEvidence, entryOperations, { invalidation: true });
        try {
          await remove(path, { force: false });
        } catch (removeError) {
          if (removeError?.code !== "ENOENT") removalError = removeError;
        }
      } catch (verificationError) {
        if (verificationError?.code === "evidence-entry-identity-changed"
          || verificationError?.code === "evidence-output-identity-changed") {
          throw verificationError;
        }
        if (verificationError?.cause?.code !== "ENOENT") throw verificationError;
      }
    }

    if (quarantined) {
      await requireDirectoryIdentity(output, lease.identity, inspect, { invalidation: true });
      await requireEvidenceEntry(quarantine, expectedEvidence, entryOperations, {
        invalidation: true,
        code: "evidence-entry-identity-changed",
        message: "Quarantined release smoke evidence no longer matches the captured evidence entry.",
      });
      try {
        await remove(quarantine, { force: true });
      } catch (error) {
        cleanupError = error;
      }
    }

    let finalEntry;
    let finalQuarantine;
    let verificationError;
    try {
      await requireDirectoryIdentity(output, lease.identity, inspect, { invalidation: true });
      finalEntry = await inspectExactEntry(path, inspect);
      finalQuarantine = await inspectExactEntry(quarantine, inspect);
    } catch (error) {
      verificationError = error;
    }

    const targetUnavailable = verificationError === undefined && finalEntry.absent;
    if (cleanupError || (quarantined && verificationError === undefined && !finalQuarantine.absent)) {
      const errors = [cleanupError, verificationError].filter(Boolean);
      if (!cleanupError && !finalQuarantine.absent) {
        errors.push(new Error("Quarantined release smoke evidence remains after cleanup."));
      }
      throw evidenceInvalidationError(
        "evidence-quarantine-cleanup-failed",
        "Prior release smoke evidence was quarantined, but quarantine cleanup failed.",
        targetUnavailable,
        errors.length > 1 ? new AggregateError(errors) : errors[0],
      );
    }
    if (verificationError) {
      throw evidenceInvalidationError(
        "evidence-invalidation-unverified",
        "Prior release smoke evidence invalidation could not be verified.",
        false,
        verificationError,
      );
    }
    if (!finalEntry.absent) {
      throw evidenceInvalidationError(
        "evidence-invalidation-failed",
        "Prior release smoke evidence remains available after invalidation.",
        false,
        removalError ? new AggregateError([moveError, removalError]) : moveError,
      );
    }
    return Object.freeze({ targetUnavailable: true, quarantineRemoved: true });
  });
}
