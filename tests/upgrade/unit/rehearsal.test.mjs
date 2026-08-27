import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { createMatchedBackup, rehearse, verifyMatchedBackup } from "../../../eng/upgrade-tests/rehearsal.mjs";
import { createRunScope } from "../../../eng/upgrade-tests/paths.mjs";

const candidateSourceSha = "0123456789abcdef0123456789abcdef01234567";
const node = process.execPath;
const repositoryRootForProcess = fileURLToPath(new URL("../../../", import.meta.url));

function successfulOperations(events, failure) {
  const operation = (name, result) => async () => {
    events.push(name);
    if (failure === name) throw new Error(`${name} invariant failed`);
    return result;
  };
  return {
    preflight: operation("preflight", {
      imageId: `sha256:${"a".repeat(64)}`,
      labels: {
        "org.opencontainers.image.version": "1.0.0",
        "org.opencontainers.image.revision": candidateSourceSha,
      },
    }),
    restoreFixture: operation("fixture:restore"),
    baseline: operation("baseline"),
    backup: operation("backup:create", { manifestSha256: "b".repeat(64) }),
    upgrade: operation("upgrade"),
    candidate: operation("candidate", { canaryId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd" }),
    backupReverify: operation("backup:verify-again"),
    discardUpgradedState: operation("upgraded-volumes:remove"),
    restoreBackup: operation("backup:restore"),
    rollback: operation("rollback"),
    captureDiagnostics: operation("diagnostics:capture"),
    cleanup: operation("owned-resources:cleanup"),
  };
}

async function runWithFakes({ fail } = {}) {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-test-"));
  const events = [];
  try {
    const report = await rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "rehearsal-test-001",
      operations: successfulOperations(events, fail),
    });
    return { events, report };
  } catch (error) {
    error.events = events;
    throw error;
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
}

test("never destroys upgraded state before re-verifying the matched backup", async () => {
  const { events } = await runWithFakes();

  assert.ok(events.indexOf("backup:verify-again") < events.indexOf("upgraded-volumes:remove"));
});

test("candidate failure still captures logs and cleans owned resources", async () => {
  await assert.rejects(
    () => runWithFakes({ fail: "candidate" }),
    (error) => {
      assert.match(error.message, /candidate invariant/i);
      assert.deepEqual(error.events.slice(-2), ["diagnostics:capture", "owned-resources:cleanup"]);
      return true;
    },
  );
});

test("failure reports expose only the exact phase contract and sanitized diagnostics", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-report-"));
  const fixtureDirectory = resolve(repositoryRoot, "fixture-secret-location");
  const secret = "cmsify_report-secret-token-value";
  const snapshots = [];
  const events = [];
  const operations = successfulOperations(events);
  operations.candidate = async () => {
    events.push("candidate");
    throw new Error(`candidate invariant exposed ${secret} at ${fixtureDirectory}`);
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory,
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "rehearsal-test-002",
      operations,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }));

    const finalReport = snapshots.at(-1);
    assert.deepEqual(finalReport.phases.map(({ name }) => name), [
      "preflight", "restore-fixture", "baseline", "backup", "upgrade", "candidate",
      "backup-reverify", "discard-upgraded-state", "restore-backup", "rollback", "cleanup",
    ]);
    assert.equal(finalReport.phases.find(({ name }) => name === "candidate").status, "failed");
    assert.equal(finalReport.phases.find(({ name }) => name === "backup-reverify").status, "pending");
    assert.equal(finalReport.phases.find(({ name }) => name === "cleanup").status, "passed");
    const serialized = JSON.stringify(finalReport);
    assert.equal(serialized.includes(secret), false);
    assert.equal(serialized.includes(repositoryRoot), false);
    assert.match(finalReport.phases.find(({ name }) => name === "candidate").error, /candidate invariant/i);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("matched backup verification fences one database and media generation", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-matched-backup-"));
  const scope = createRunScope(repositoryRoot, "matched-backup-001");
  const controller = new AbortController();
  const operationOptions = [];
  const harness = {
    exec: async (_service, _args, options) => {
      operationOptions.push(options);
      return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    },
    copyFrom: async (service, _source, destination, options) => {
      operationOptions.push(options);
      if (service === "postgres") {
        writeFileSync(destination, "database backup bytes", "utf8");
        return;
      }
      mkdirSync(resolve(destination, "cmsify", "media"), { recursive: true });
      writeFileSync(resolve(destination, "cmsify", "media", "first.txt"), "first media", "utf8");
      writeFileSync(resolve(destination, "cmsify", "media", "second.bin"), Buffer.from([0, 1, 2, 3]));
    },
  };

  try {
    const fence = await createMatchedBackup({
      harness,
      scope,
      baselineVersion: "0.1.3",
      now: () => "2026-08-27T12:00:00.000Z",
      signal: controller.signal,
      redact: ["fixture-backup-secret"],
    });
    const manifest = JSON.parse(readFileSync(resolve(scope.diagnosticsDirectory, "backup", "backup-manifest.json"), "utf8"));

    assert.equal(manifest.runId, scope.runId);
    assert.equal(manifest.baselineVersion, "0.1.3");
    assert.match(manifest.databaseSha256, /^[0-9a-f]{64}$/);
    assert.deepEqual(manifest.mediaObjects.map(({ path }) => path), ["cmsify/media/first.txt", "cmsify/media/second.bin"]);
    assert.equal(operationOptions.every((options) => options.signal === controller.signal), true);
    assert.equal(operationOptions.every((options) => options.redact[0] === "fixture-backup-secret"), true);
    await verifyMatchedBackup({ scope, baselineVersion: "0.1.3", manifestSha256: fence.manifestSha256 });

    writeFileSync(resolve(scope.diagnosticsDirectory, "backup", "media", "cmsify", "media", "first.txt"), "changed", "utf8");
    await assert.rejects(
      verifyMatchedBackup({ scope, baselineVersion: "0.1.3", manifestSha256: fence.manifestSha256 }),
      /backup media checksum mismatch/i,
    );
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("default preflight validates fixture and every image before the first resource mutation", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-preflight-"));
  const fixtureDirectory = resolve(repositoryRoot, "fixture");
  const events = [];
  const imageId = `sha256:${"c".repeat(64)}`;
  const immutableImage = (repository) => ({
    repository,
    tag: "test",
    digest: `sha256:${"d".repeat(64)}`,
    platform: "linux/amd64",
  });
  const manifest = {
    baseline: {
      version: "0.1.3",
      apiImage: immutableImage("baseline-api"),
      postgresImage: immutableImage("postgres"),
      minioImage: immutableImage("minio"),
    },
  };
  const harness = {
    inspectImage: async (image) => events.push(`inspect:${image.repository}`),
    inspectCandidateImage: async () => {
      events.push("inspect:candidate");
      return {
        reference: "cmsify-candidate:test",
        imageId,
        platform: "linux/amd64",
        version: "1.0.0",
        sourceSha: candidateSourceSha,
        informationalVersion: `1.0.0+${candidateSourceSha}`,
        labels: {
          "org.opencontainers.image.version": "1.0.0",
          "org.opencontainers.image.revision": candidateSourceSha,
        },
      };
    },
    writeEnvironment: async (values) => {
      events.push("environment:write");
      assert.equal(values.CANDIDATE_API_IMAGE, imageId);
      assert.equal(values.CANDIDATE_API_IMAGE_REFERENCE, "cmsify-candidate:test");
      assert.equal(values.CANDIDATE_API_IMAGE_ID, imageId);
    },
    up: async () => {
      events.push("docker:up");
      throw new Error("restore fixture stop");
    },
    logs: async () => events.push("diagnostics:capture"),
    cleanup: async () => events.push("owned-resources:cleanup"),
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory,
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "preflight-test-001",
      dependencies: {
        createDockerHarness: () => harness,
        loadFixtureManifest: () => {
          events.push("manifest:validate");
          return manifest;
        },
        loadExpectedData: async () => {
          events.push("expected:validate");
          return { authentication: { readerToken: "cmsify_fixture-reader", adminPassword: "fixture-admin-password" } };
        },
        verifyFixtureChecksums: async () => events.push("checksums:verify"),
      },
    }), /restore fixture stop/i);

    assert.deepEqual(events.slice(0, 8), [
      "manifest:validate",
      "expected:validate",
      "checksums:verify",
      "inspect:baseline-api",
      "inspect:postgres",
      "inspect:minio",
      "inspect:candidate",
      "environment:write",
    ]);
    assert.equal(events.indexOf("inspect:candidate") < events.indexOf("docker:up"), true);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("default operations pass the candidate canary through isolated backup rollback", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-default-"));
  const fixtureDirectory = resolve(repositoryRoot, "fixture");
  const events = [];
  const canaryId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
  const controller = new AbortController();
  const imageId = `sha256:${"e".repeat(64)}`;
  const image = (repository) => ({ repository, tag: "test", digest: `sha256:${"f".repeat(64)}`, platform: "linux/amd64" });
  const manifest = {
    baseline: {
      version: "0.1.3",
      sourceSha: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
      apiImage: image("baseline-api"),
      postgresImage: image("postgres"),
      minioImage: image("minio"),
    },
  };
  const expected = {
    ids: { primaryWorkspace: "11111111-1111-4111-8111-111111111111" },
    relatedIds: {},
    authentication: { readerToken: "cmsify_fixture-reader", adminPassword: "fixture-admin-password" },
  };
  const harness = {
    inspectImage: async (value) => events.push(`inspect:${value.repository}`),
    inspectCandidateImage: async () => ({
      reference: "cmsify-candidate:test",
      imageId,
      platform: "linux/amd64",
      version: "1.0.0",
      sourceSha: candidateSourceSha,
      informationalVersion: `1.0.0+${candidateSourceSha}`,
      labels: {
        "org.opencontainers.image.version": "1.0.0",
        "org.opencontainers.image.revision": candidateSourceSha,
      },
    }),
    writeEnvironment: async () => events.push("environment:write"),
    up: async (services) => events.push(`up:${services.join(",")}`),
    stop: async (service) => events.push(`stop:${service}`),
    exec: async (service, args, options) => {
      events.push(`exec:${service}:${args[0]}`);
      if (args[0] === "assertion-probe") assert.equal(options.signal, controller.signal);
      return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    },
    copyTo: async (service, source) => events.push(`copy:${service}:${source.includes("backup") ? "backup" : "fixture"}`),
    discardDataVolumes: async () => events.push("upgraded-volumes:remove"),
    logs: async () => events.push("diagnostics:capture"),
    cleanup: async () => events.push("owned-resources:cleanup"),
  };

  try {
    const report = await rehearse({
      repositoryRoot,
      fixtureDirectory,
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "default-test-001",
      signal: controller.signal,
      dependencies: {
        createDockerHarness: () => harness,
        loadFixtureManifest: () => manifest,
        loadExpectedData: async () => expected,
        verifyFixtureChecksums: async () => undefined,
        captureWebhookWorkerState: async (docker) => {
          events.push("webhook:snapshot");
          await docker.exec("postgres", ["assertion-probe"]);
          return "worker-state";
        },
        assertBaseline: async (context) => {
          events.push("assert:baseline");
          await context.docker.exec("postgres", ["assertion-probe"]);
          return { phase: "baseline", assertions: [] };
        },
        assertCandidate: async (context) => {
          events.push("assert:candidate");
          assert.equal(context.candidate.imageId, imageId);
          await context.docker.exec("postgres", ["assertion-probe"]);
          return { phase: "candidate", assertions: [], canaryId };
        },
        assertRollback: async (context) => {
          events.push("assert:rollback");
          assert.equal(context.canaryId, canaryId);
          await context.docker.exec("postgres", ["assertion-probe"]);
          return { phase: "rollback", assertions: [] };
        },
        createMatchedBackup: async () => {
          events.push("backup:create");
          return { manifestSha256: "1".repeat(64) };
        },
        verifyMatchedBackup: async ({ manifestSha256 }) => {
          events.push("backup:verify-again");
          assert.equal(manifestSha256, "1".repeat(64));
        },
      },
    });

    assert.equal(report.status, "passed");
    assert.equal(report.canaryId, canaryId);
    assert.equal(report.phases.every(({ status }) => status === "passed"), true);
    assert.deepEqual(events.filter((event) => event.startsWith("up:candidate")), ["up:candidate-api"]);
    assert.ok(events.indexOf("backup:verify-again") < events.indexOf("upgraded-volumes:remove"));
    const rollbackBaselineStart = events.lastIndexOf("up:baseline-api");
    assert.ok(events.indexOf("upgraded-volumes:remove") < events.lastIndexOf("copy:postgres:backup"));
    assert.ok(events.lastIndexOf("copy:postgres:backup") < rollbackBaselineStart);
    assert.ok(rollbackBaselineStart < events.indexOf("assert:rollback"));
    assert.equal(events.at(-1), "owned-resources:cleanup");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("CLI parses the exact rehearsal contract and reports the run without leaking internals", () => {
  const script = `
    import { main } from "./eng/upgrade-tests/cli.mjs";
    const exitCode = await main([
      "rehearse", "--fixture", "tests/upgrade/fixtures/v0.1.3",
      "--candidate-image", "cmsify-candidate:test", "--candidate-version", "1.0.0",
      "--candidate-source-sha", "${candidateSourceSha}"
    ], {
      rehearse: async (options) => {
        process.stdout.write("OPTIONS=" + JSON.stringify({
          fixtureDirectory: options.fixtureDirectory.replaceAll("\\\\", "/"),
          candidateImage: options.candidateImage,
          candidateVersion: options.candidateVersion,
          candidateSourceSha: options.candidateSourceSha,
        }) + "\\n");
        return { runId: "cli-test-run", reportPath: "should-not-be-used" };
      },
    });
    process.exitCode = exitCode;
  `;

  const result = spawnSync(node, ["--input-type=module", "--eval", script], { cwd: repositoryRootForProcess, encoding: "utf8" });

  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /"candidateImage":"cmsify-candidate:test"/);
  assert.match(result.stdout, /"candidateVersion":"1.0.0"/);
  assert.match(result.stdout, new RegExp(candidateSourceSha));
  assert.match(result.stdout, /Rehearsal passed.*cli-test-run/i);
  assert.equal(result.stdout.includes("should-not-be-used"), false);
});

test("CLI process rejects missing and malformed rehearsal identities before Docker", () => {
  const cli = resolve(repositoryRootForProcess, "eng", "upgrade-tests", "cli.mjs");
  const missing = spawnSync(node, [cli, "rehearse", "--fixture", "fixture"], { cwd: repositoryRootForProcess, encoding: "utf8" });
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /Usage:/);
  assert.equal(missing.stderr.includes("Error:"), false);

  const malformed = spawnSync(node, [
    cli, "rehearse", "--fixture", "fixture",
    "--candidate-image", "cmsify-candidate:test", "--candidate-version", "01.0.0",
    "--candidate-source-sha", candidateSourceSha,
  ], { cwd: repositoryRootForProcess, encoding: "utf8" });
  assert.equal(malformed.status, 1);
  assert.match(malformed.stderr, /valid SemVer/i);
  assert.equal(malformed.stderr.includes(repositoryRootForProcess), false);
});

test("CLI process forwards cancellation and sanitizes its failure", () => {
  const secret = "cmsify_cancellation-secret-token";
  const script = `
    import { main } from "./eng/upgrade-tests/cli.mjs";
    const controller = new AbortController();
    controller.abort();
    const exitCode = await main([
      "rehearse", "--fixture", "tests/upgrade/fixtures/v0.1.3",
      "--candidate-image", "cmsify-candidate:test", "--candidate-version", "1.0.0",
      "--candidate-source-sha", "${candidateSourceSha}"
    ], {
      signal: controller.signal,
      rehearse: async (options) => {
        if (!options.signal.aborted) throw new Error("signal was not cancelled");
        throw new Error("Rehearsal cancelled with ${secret} at " + options.fixtureDirectory);
      },
    });
    process.exitCode = exitCode;
  `;
  const result = spawnSync(node, ["--input-type=module", "--eval", script], { cwd: repositoryRootForProcess, encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /Rehearsal cancelled/i);
  assert.equal(result.stderr.includes(secret), false);
  assert.equal(result.stderr.includes(repositoryRootForProcess), false);
  assert.equal(result.stderr.includes("<fixture>"), true);
});

test("pre-cancelled default rehearsal records failure and cleans without inspecting or starting images", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-cancelled-"));
  const controller = new AbortController();
  controller.abort();
  const events = [];
  const snapshots = [];
  const harness = {
    inspectImage: async () => events.push("image:inspect"),
    logs: async () => events.push("diagnostics:capture"),
    cleanup: async () => events.push("owned-resources:cleanup"),
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cancelled-test-001",
      signal: controller.signal,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
      dependencies: { createDockerHarness: () => harness },
    }), /cancelled/i);

    assert.deepEqual(events, ["diagnostics:capture", "owned-resources:cleanup"]);
    assert.equal(snapshots.at(-1).phases[0].status, "failed");
    assert.equal(snapshots.at(-1).phases.at(-1).status, "passed");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("timeout stops mandatory phases but diagnostics and cleanup still run", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-timeout-"));
  const events = [];
  const snapshots = [];
  const operations = successfulOperations(events);
  operations.upgrade = async () => {
    events.push("upgrade");
    throw new Error("Candidate migration timed out; password=timeout-secret-value");
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "timeout-test-001",
      operations,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }), /timed out/i);

    assert.deepEqual(events.slice(-2), ["diagnostics:capture", "owned-resources:cleanup"]);
    const finalReport = snapshots.at(-1);
    assert.equal(finalReport.phases.find(({ name }) => name === "upgrade").status, "failed");
    assert.equal(finalReport.phases.find(({ name }) => name === "candidate").status, "pending");
    assert.equal(JSON.stringify(finalReport).includes("timeout-secret-value"), false);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("cleanup failure never replaces the primary rehearsal failure", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-cleanup-"));
  const events = [];
  const operations = successfulOperations(events, "candidate");
  operations.cleanup = async () => {
    events.push("owned-resources:cleanup");
    throw new Error("cleanup-only-secret");
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cleanup-test-001",
      operations,
    }), (error) => {
      assert.match(error.message, /candidate invariant failed/i);
      assert.equal(error.message.includes("cleanup-only-secret"), false);
      assert.ok(error.cause instanceof AggregateError);
      assert.match(error.cause.errors[0].message, /candidate invariant failed/i);
      assert.match(error.cause.errors[1].message, /cleanup-only-secret/i);
      return true;
    });
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("every successful phase transition is persisted running before passed", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-transitions-"));
  const snapshots = [];
  try {
    const report = await rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "transition-test-001",
      operations: successfulOperations([]),
      reportWriter: async (value) => snapshots.push(structuredClone(value)),
    });

    assert.equal(snapshots.length, 22, "each of 11 phases must persist exactly its running and passed transitions");
    for (const phase of report.phases) {
      const statuses = snapshots.map((snapshot) => snapshot.phases.find(({ name }) => name === phase.name).status);
      assert.ok(statuses.indexOf("running") >= 0);
      assert.ok(statuses.indexOf("running") < statuses.indexOf("passed"));
    }
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("direct rehearsal rejects malformed candidate syntax before image inspection", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-invalid-"));
  const events = [];
  const harness = {
    inspectImage: async () => events.push("image:inspect"),
    logs: async () => events.push("diagnostics:capture"),
    cleanup: async () => events.push("owned-resources:cleanup"),
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "01.0.0",
      candidateSourceSha,
      runId: "invalid-test-001",
      dependencies: {
        createDockerHarness: () => harness,
        loadFixtureManifest: () => ({ baseline: {} }),
        loadExpectedData: async () => ({ authentication: {} }),
        verifyFixtureChecksums: async () => undefined,
      },
    }), /SemVer/i);

    assert.deepEqual(events, ["diagnostics:capture", "owned-resources:cleanup"]);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("report persistence failure cannot prevent diagnostics and owned cleanup", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-report-failure-"));
  const events = [];
  const operations = successfulOperations(events);

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "report-failure-001",
      operations,
      reportWriter: async () => { throw new Error("report storage unavailable"); },
    }), /report storage unavailable/i);

    assert.deepEqual(events, ["diagnostics:capture", "owned-resources:cleanup"]);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});
