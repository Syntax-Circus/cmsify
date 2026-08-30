import { mkdir, open, rename, rm, stat } from "node:fs/promises";
import { dirname, parse, resolve } from "node:path";
import { randomBytes } from "node:crypto";

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

function evidenceInvalidationError(code, message, targetUnavailable, cause) {
  const error = new Error(message, cause ? { cause } : undefined);
  error.name = "EvidenceInvalidationError";
  error.code = code;
  error.targetUnavailable = targetUnavailable;
  return error;
}

async function isUnavailable(path, inspect) {
  try {
    await inspect(path);
    return false;
  } catch (error) {
    if (error?.code === "ENOENT") return true;
    throw error;
  }
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

export async function writeEvidence(outputDirectory, evidence) {
  const { output, path } = resolveEvidenceTarget(outputDirectory);
  await mkdir(output, { recursive: true, mode: 0o700 });
  const temporary = resolve(output, `.evidence-${process.pid}-${randomBytes(6).toString("hex")}.tmp`);
  let handle;
  try {
    handle = await open(temporary, "wx", 0o600);
    await handle.writeFile(`${JSON.stringify(evidence, null, 2)}\n`, "utf8");
    await handle.sync();
    await handle.close();
    handle = undefined;
    await rename(temporary, path);
    return path;
  } finally {
    await handle?.close().catch(() => {});
    await rm(temporary, { force: true }).catch(() => {});
  }
}

export async function invalidateEvidence(outputDirectory, operations = {}) {
  const { output, path } = resolveEvidenceTarget(outputDirectory);
  const move = operations.rename ?? rename;
  const remove = operations.rm ?? rm;
  const inspect = operations.stat ?? stat;
  const quarantine = resolve(output, `.evidence-invalid-${process.pid}-${randomBytes(6).toString("hex")}.quarantine`);
  assert(dirname(quarantine) === output, "Release smoke evidence quarantine path escaped its output directory.");

  try {
    await move(path, quarantine);
  } catch (moveError) {
    if (moveError?.code === "ENOENT") {
      const unavailable = await isUnavailable(path, inspect);
      if (unavailable) return Object.freeze({ targetUnavailable: true, quarantineRemoved: true });
    }

    try {
      await remove(path, { force: false });
    } catch (removeError) {
      let unavailable = false;
      try {
        unavailable = await isUnavailable(path, inspect);
      } catch (inspectError) {
        throw evidenceInvalidationError(
          "evidence-invalidation-unverified",
          "Prior release smoke evidence invalidation could not be verified.",
          false,
          new AggregateError([moveError, removeError, inspectError]),
        );
      }
      if (!unavailable) {
        throw evidenceInvalidationError(
          "evidence-invalidation-failed",
          "Prior release smoke evidence remains available after invalidation failed.",
          false,
          new AggregateError([moveError, removeError]),
        );
      }
    }

    if (!await isUnavailable(path, inspect)) {
      throw evidenceInvalidationError(
        "evidence-invalidation-failed",
        "Prior release smoke evidence remains available after invalidation.",
        false,
        moveError,
      );
    }
    return Object.freeze({ targetUnavailable: true, quarantineRemoved: true });
  }

  let targetUnavailable;
  try {
    targetUnavailable = await isUnavailable(path, inspect);
  } catch (inspectError) {
    throw evidenceInvalidationError(
      "evidence-invalidation-unverified",
      "Quarantined release smoke evidence invalidation could not be verified.",
      false,
      inspectError,
    );
  }
  if (!targetUnavailable) {
    throw evidenceInvalidationError(
      "evidence-invalidation-failed",
      "Prior release smoke evidence remained at its consumable path after quarantine.",
      false,
    );
  }

  try {
    await remove(quarantine, { force: true });
  } catch (cleanupError) {
    throw evidenceInvalidationError(
      "evidence-quarantine-cleanup-failed",
      "Prior release smoke evidence was quarantined, but quarantine cleanup failed.",
      true,
      cleanupError,
    );
  }
  return Object.freeze({ targetUnavailable: true, quarantineRemoved: true });
}
