import assert from "node:assert/strict";
import { lstat, mkdir, readFile, readdir, rm, stat, symlink, writeFile } from "node:fs/promises";
import { basename, parse, resolve } from "node:path";
import test from "node:test";

import {
  createEvidence,
  invalidateEvidence,
  sanitizeFailure,
  writeEvidence,
} from "../../eng/release-smoke/evidence.mjs";
import { RELEASE_SMOKE_SCENARIOS } from "../../eng/release-smoke/harness.mjs";

const version = "1.2.3";
const sourceSha = "0123456789abcdef0123456789abcdef01234567";
const candidates = {
  api: { reference: "repo/api:1.2.3", imageId: `sha256:${"a".repeat(64)}`, digest: `sha256:${"b".repeat(64)}` },
  admin: { reference: "repo/admin:1.2.3", imageId: `sha256:${"c".repeat(64)}`, digest: `sha256:${"d".repeat(64)}` },
};

test("emits the bounded cmsify.release-smoke.v1 evidence contract", () => {
  const evidence = createEvidence({
    version,
    sourceSha,
    runId: "cmsify-smoke-1234abcd",
    candidates,
    startedAt: "2026-08-29T18:00:00.000Z",
    completedAt: "2026-08-29T18:00:10.000Z",
    status: "passed",
    scenarios: RELEASE_SMOKE_SCENARIOS.map((name, index) => ({ name, status: "passed", durationMs: index + 1 })),
    backupHashes: { postgresSha256: "1".repeat(64), mediaSha256: "2".repeat(64) },
    cleanup: { status: "passed" },
  });

  assert.equal(evidence.schema, "cmsify.release-smoke.v1");
  assert.equal(evidence.schemaVersion, 1);
  assert.equal(evidence.version, version);
  assert.equal(evidence.sourceSha, sourceSha);
  assert.deepEqual(evidence.candidates, candidates);
  assert.deepEqual(evidence.scenarios.map(({ name }) => name), RELEASE_SMOKE_SCENARIOS);
  assert.deepEqual(evidence.backupHashes, { postgresSha256: "1".repeat(64), mediaSha256: "2".repeat(64) });
  assert.equal(evidence.failure, null);
  assert.equal("credentials" in evidence, false);
  assert.equal("payload" in evidence, false);
});

test("redacts supplied and credential-shaped sentinels without retaining stack or payload bodies", () => {
  const secret = "sentinel-password-DO-NOT-LEAK";
  const error = new Error(`Authorization: Bearer cmsify_API_TOKEN_VALUE password=${secret} whsec_WEBHOOK_VALUE`);
  error.stack = `${error.message}\npayload={\"secret\":\"${secret}\"}`;

  const failure = sanitizeFailure(error, {
    scenario: "local-login",
    redactions: [secret],
  });

  const serialized = JSON.stringify(failure);
  assert.equal(failure.scenario, "local-login");
  assert.equal("stack" in failure, false);
  assert.equal("payload" in failure, false);
  assert.doesNotMatch(serialized, /DO-NOT-LEAK|cmsify_API_TOKEN_VALUE|whsec_WEBHOOK_VALUE|Authorization: Bearer/);
  assert.match(failure.message, /<redacted>/);
});

test("rejects malformed hashes, candidate identities, and unexpected scenario names", () => {
  const base = {
    version,
    sourceSha,
    runId: "cmsify-smoke-1234abcd",
    candidates,
    startedAt: "2026-08-29T18:00:00.000Z",
    completedAt: "2026-08-29T18:00:10.000Z",
    status: "passed",
    scenarios: RELEASE_SMOKE_SCENARIOS.map((name) => ({ name, status: "passed", durationMs: 1 })),
    backupHashes: { postgresSha256: "1".repeat(64), mediaSha256: "2".repeat(64) },
    cleanup: { status: "passed" },
  };
  assert.throws(() => createEvidence({ ...base, backupHashes: { postgresSha256: "bad", mediaSha256: "2".repeat(64) } }), /backup hash/i);
  assert.throws(() => createEvidence({ ...base, candidates: { ...candidates, api: { ...candidates.api, imageId: "latest" } } }), /candidate/i);
  assert.throws(() => createEvidence({ ...base, scenarios: [...base.scenarios.slice(0, -1), { name: "invented", status: "passed", durationMs: 1 }] }), /scenario/i);
});

test("writes evidence atomically below the requested run-owned directory", async () => {
  const output = resolve("artifacts/release-smoke/evidence-unit-test");
  await rm(output, { recursive: true, force: true });
  const evidence = createEvidence({
    version,
    sourceSha,
    runId: "cmsify-smoke-1234abcd",
    candidates,
    startedAt: "2026-08-29T18:00:00.000Z",
    completedAt: "2026-08-29T18:00:10.000Z",
    status: "passed",
    scenarios: RELEASE_SMOKE_SCENARIOS.map((name) => ({ name, status: "passed", durationMs: 1 })),
    backupHashes: { postgresSha256: "1".repeat(64), mediaSha256: "2".repeat(64) },
    cleanup: { status: "passed" },
  });

  const path = await writeEvidence(output, evidence);
  assert.equal(path, resolve(output, "evidence.json"));
  assert.deepEqual(JSON.parse(await readFile(path, "utf8")), evidence);
  await rm(output, { recursive: true, force: true });
});

test("invalidates only the exact evidence target while preserving sibling output", async () => {
  const output = resolve("artifacts/release-smoke/evidence-invalidation-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  const siblingPath = resolve(output, "keep.txt");
  await rm(output, { recursive: true, force: true });
  try {
    await writeEvidence(output, { status: "passed" });
    await writeFile(siblingPath, "keep", "utf8");

    const result = await invalidateEvidence(output);

    assert.equal(result.targetUnavailable, true);
    await assert.rejects(readFile(evidencePath, "utf8"));
    assert.equal(await readFile(siblingPath, "utf8"), "keep");
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("evidence invalidation refuses filesystem roots before touching a target", async () => {
  await assert.rejects(
    invalidateEvidence(resolve(process.cwd(), parse(process.cwd()).root)),
    /filesystem root/i,
  );
});

test("evidence invalidation requires an existing real output directory", async () => {
  const output = resolve("artifacts/release-smoke/evidence-missing-output-unit-test");
  await rm(output, { recursive: true, force: true });

  await assert.rejects(
    invalidateEvidence(output),
    (error) => error.code === "evidence-output-unavailable" && error.targetUnavailable === false,
  );
});

test("evidence invalidation rejects a directory symlink or junction before mutating its target", async () => {
  const root = resolve("artifacts/release-smoke/evidence-output-link-unit-test");
  const target = resolve(root, "target");
  const output = resolve(root, "output-link");
  const targetEvidence = resolve(target, "evidence.json");
  await rm(root, { recursive: true, force: true });
  try {
    await mkdir(target, { recursive: true });
    await writeFile(targetEvidence, "passed", "utf8");
    await symlink(target, output, process.platform === "win32" ? "junction" : "dir");

    await assert.rejects(
      invalidateEvidence(output),
      (error) => error.code === "evidence-output-indirection" && error.targetUnavailable === false,
    );

    assert.equal(await readFile(targetEvidence, "utf8"), "passed");
    assert.deepEqual(await readdir(target), ["evidence.json"]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("evidence invalidation rejects a Windows junction component before mutating through it", { skip: process.platform !== "win32" }, async () => {
  const root = resolve("artifacts/release-smoke/evidence-output-junction-unit-test");
  const targetParent = resolve(root, "target-parent");
  const targetOutput = resolve(targetParent, "output");
  const junctionParent = resolve(root, "junction-parent");
  const output = resolve(junctionParent, "output");
  const targetEvidence = resolve(targetOutput, "evidence.json");
  await rm(root, { recursive: true, force: true });
  try {
    await mkdir(targetOutput, { recursive: true });
    await writeFile(targetEvidence, "passed", "utf8");
    await symlink(targetParent, junctionParent, "junction");

    await assert.rejects(
      invalidateEvidence(output),
      (error) => error.code === "evidence-output-indirection" && error.targetUnavailable === false,
    );

    assert.equal(await readFile(targetEvidence, "utf8"), "passed");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("evidence invalidation rejects an output link whose canonical target is a filesystem root before mutation", async () => {
  const root = resolve("artifacts/release-smoke/evidence-output-root-link-unit-test");
  const output = resolve(root, "output-link");
  const mutations = [];
  await rm(root, { recursive: true, force: true });
  try {
    await mkdir(root, { recursive: true });
    await symlink(parse(output).root, output, process.platform === "win32" ? "junction" : "dir");

    await assert.rejects(
      invalidateEvidence(output, {
        rename: async (...args) => {
          mutations.push(["rename", ...args]);
          throw Object.assign(new Error("mutation must not run"), { code: "EACCES" });
        },
        rm: async (...args) => {
          mutations.push(["rm", ...args]);
          throw Object.assign(new Error("mutation must not run"), { code: "EACCES" });
        },
      }),
      (error) => error.targetUnavailable === false && /filesystem root|indirection/i.test(error.message),
    );

    assert.deepEqual(mutations, []);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("a dangling evidence link is removed rather than misclassified as absent after a rename miss", async () => {
  const output = resolve("artifacts/release-smoke/evidence-dangling-link-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  const missingTarget = resolve(output, "missing-target");
  await rm(output, { recursive: true, force: true });
  try {
    await mkdir(missingTarget, { recursive: true });
    await symlink(missingTarget, evidencePath, process.platform === "win32" ? "junction" : "dir");
    await rm(missingTarget, { recursive: true, force: true });

    const result = await invalidateEvidence(output, {
      rename: async () => { throw Object.assign(new Error("simulated rename miss"), { code: "ENOENT" }); },
    });

    assert.equal(result.targetUnavailable, true);
    await assert.rejects(lstat(evidencePath), (error) => error.code === "ENOENT");
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("cleanup-time writeEvidence reentrancy cannot recreate evidence before invalidation returns", async () => {
  const output = resolve("artifacts/release-smoke/evidence-reentrant-write-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  let recreationError;
  await rm(output, { recursive: true, force: true });
  try {
    await writeEvidence(output, { status: "passed" });

    const result = await invalidateEvidence(output, {
      rm: async (path, options) => {
        if (basename(path).startsWith(".evidence-invalid-")) {
          try {
            await writeEvidence(output, { status: "recreated" });
          } catch (error) {
            recreationError = error;
          }
        }
        return rm(path, options);
      },
    });

    assert.equal(result.targetUnavailable, true);
    assert.equal(recreationError?.code, "evidence-operation-conflict");
    await assert.rejects(lstat(evidencePath), (error) => error.code === "ENOENT");
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("quarantine cleanup failure is surfaced after the consumable evidence target is unavailable", async () => {
  const output = resolve("artifacts/release-smoke/evidence-quarantine-cleanup-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  await rm(output, { recursive: true, force: true });
  try {
    await writeEvidence(output, { status: "passed" });

    await assert.rejects(
      invalidateEvidence(output, {
        rm: async (path, options) => {
          if (basename(path).startsWith(".evidence-invalid-")) throw new Error("cleanup refused");
          return rm(path, options);
        },
      }),
      (error) => error.code === "evidence-quarantine-cleanup-failed" && error.targetUnavailable === true,
    );

    await assert.rejects(readFile(evidencePath, "utf8"));
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("quarantine cleanup failure reports target unavailable false when cleanup recreates evidence", async () => {
  const output = resolve("artifacts/release-smoke/evidence-quarantine-recreation-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  await rm(output, { recursive: true, force: true });
  try {
    await writeEvidence(output, { status: "passed" });

    await assert.rejects(
      invalidateEvidence(output, {
        rm: async (path, options) => {
          if (basename(path).startsWith(".evidence-invalid-")) {
            await writeFile(evidencePath, "recreated", "utf8");
            throw new Error("cleanup refused");
          }
          return rm(path, options);
        },
      }),
      (error) => error.code === "evidence-quarantine-cleanup-failed" && error.targetUnavailable === false,
    );

    assert.equal(await readFile(evidencePath, "utf8"), "recreated");
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("quarantine cleanup completes before a final no-follow verification failure is reported", async () => {
  const output = resolve("artifacts/release-smoke/evidence-final-verification-unit-test");
  const evidencePath = resolve(output, "evidence.json");
  let quarantineRemoved = false;
  const inspect = async (path) => {
    if (path === evidencePath) throw Object.assign(new Error("inspection refused"), { code: "EACCES" });
    return stat(path);
  };
  await rm(output, { recursive: true, force: true });
  try {
    await writeEvidence(output, { status: "passed" });

    await assert.rejects(
      invalidateEvidence(output, {
        lstat: inspect,
        rm: async (path, options) => {
          if (basename(path).startsWith(".evidence-invalid-")) quarantineRemoved = true;
          return rm(path, options);
        },
      }),
      (error) => error.code === "evidence-invalidation-unverified" && error.targetUnavailable === false,
    );

    assert.equal(quarantineRemoved, true);
    assert.deepEqual(await readdir(output), []);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});
