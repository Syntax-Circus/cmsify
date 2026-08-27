import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, linkSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { createMatchedBackup, rehearse, validateCandidateInput, verifyMatchedBackup } from "../../../eng/upgrade-tests/rehearsal.mjs";
import { createDockerHarness } from "../../../eng/upgrade-tests/docker.mjs";
import { createRunScope } from "../../../eng/upgrade-tests/paths.mjs";

const candidateSourceSha = "0123456789abcdef0123456789abcdef01234567";
const fixtureDigest = "9".repeat(64);
const baselineImage = Object.freeze({
  repository: "docker.io/syntaxcircus/cmsify-api",
  tag: "0.1.3",
  digest: `sha256:${"8".repeat(64)}`,
  platform: "linux/amd64",
});
const verifiedChecksums = () => new Map([["manifest.json", "7".repeat(64)]]);
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
      fixtureDigest,
      baselineImage,
      labels: {
        "org.opencontainers.image.version": "1.0.0",
        "org.opencontainers.image.revision": candidateSourceSha,
      },
    }),
    restoreFixture: operation("fixture:restore"),
    baseline: operation("baseline"),
    backup: operation("backup:create", { manifestSha256: "b".repeat(64) }),
    upgrade: operation("upgrade"),
    candidate: operation("candidate", { canaryId: "dddddddd-dddd-7ddd-8ddd-dddddddddddd" }),
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

test("successful report binds the verified fixture and exact baseline and candidate images", async () => {
  const { report } = await runWithFakes();

  assert.equal(report.result, "passed");
  assert.equal(report.fixtureDigest, fixtureDigest);
  assert.deepEqual(report.baselineImage, baselineImage);
  assert.deepEqual(report.candidate, {
    reference: "cmsify-candidate:test",
    version: "1.0.0",
    sourceSha: candidateSourceSha,
    imageId: `sha256:${"a".repeat(64)}`,
    platform: null,
    informationalVersion: null,
  });
});

test("candidate failure still captures logs and cleans owned resources", async () => {
  await assert.rejects(
    () => runWithFakes({ fail: "candidate" }),
    (error) => {
      assert.match(error.message, /candidate phase invariant/i);
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
    assert.equal(finalReport.phases.find(({ name }) => name === "candidate").error, "The candidate phase failed; diagnostic detail withheld.");
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
    exec: async (service, args, options) => {
      operationOptions.push(options);
      if (service === "minio" && args[0] === "mc" && args[1] === "ls") return {
        exitCode: 0,
        stdout: [
          JSON.stringify({ key: "cmsify/media/first.txt", size: Buffer.byteLength("first media"), type: "file" }),
          JSON.stringify({ key: "cmsify/media/second.bin", size: 4, type: "file" }),
        ].join("\n") + "\n",
        stderr: "",
        durationMs: 0,
      };
      if (service === "minio" && args[0] === "sha256sum") {
        const body = args[1].includes(createHash("sha256").update("cmsify/media/first.txt").digest("hex")) ? "first media" : Buffer.from([0, 1, 2, 3]);
        return { exitCode: 0, stdout: `${createHash("sha256").update(body).digest("hex")}  object\n`, stderr: "", durationMs: 0 };
      }
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
      /backup media (inventory|checksum) mismatch/i,
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
    verifyPrerequisites: async () => events.push("prerequisites:verify"),
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
        verifyFixtureChecksums: async () => {
          events.push("checksums:verify");
          return verifiedChecksums();
        },
      },
    }), /restore-fixture phase failed/i);

    assert.deepEqual(events.slice(0, 9), [
      "manifest:validate",
      "expected:validate",
      "checksums:verify",
      "inspect:baseline-api",
      "inspect:postgres",
      "inspect:minio",
      "inspect:candidate",
      "prerequisites:verify",
      "environment:write",
    ]);
    assert.equal(events.indexOf("inspect:candidate") < events.indexOf("docker:up"), true);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("default preflight failure for a missing tool cannot write the run env or start resources", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-prerequisite-"));
  const events = [];
  const image = (repository) => ({ repository, digest: `sha256:${"c".repeat(64)}`, platform: "linux/amd64" });
  const manifest = {
    baseline: {
      version: "0.1.3",
      sourceSha: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
      apiImage: image("baseline-api"),
      postgresImage: image("postgres"),
      minioImage: image("minio"),
    },
  };
  const harness = {
    inspectImage: async () => events.push("image:inspect"),
    inspectCandidateImage: async () => ({
      reference: "cmsify-candidate:test",
      imageId: `sha256:${"d".repeat(64)}`,
      platform: "linux/amd64",
      version: "1.0.0",
      sourceSha: candidateSourceSha,
      informationalVersion: `1.0.0+${candidateSourceSha}`,
    }),
    verifyPrerequisites: async () => {
      events.push("prerequisites:verify");
      throw new Error("Docker prerequisite check failed.");
    },
    writeEnvironment: async () => events.push("environment:write"),
    up: async () => events.push("docker:up"),
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
      runId: "missing-prerequisite-001",
      dependencies: {
        createDockerHarness: () => harness,
        loadFixtureManifest: () => manifest,
        loadExpectedData: async () => ({ authentication: { readerToken: "cmsify_fixture-reader", adminPassword: "fixture-password" } }),
        verifyFixtureChecksums: async () => verifiedChecksums(),
      },
    }), /prerequisite/i);

    assert.equal(events.includes("environment:write"), false);
    assert.equal(events.includes("docker:up"), false);
    assert.deepEqual(events.slice(-2), ["diagnostics:capture", "owned-resources:cleanup"]);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("default preflight reports an aborted immutable-image tool probe as cancellation", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-prerequisite-cancel-"));
  const controller = new AbortController();
  const digest = `sha256:${"c".repeat(64)}`;
  const image = (repository) => ({ repository, digest, platform: "linux/amd64" });
  const manifest = {
    baseline: {
      version: "0.1.3",
      sourceSha: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
      apiImage: image("baseline-api"),
      postgresImage: image("postgres"),
      minioImage: image("minio"),
    },
  };
  const candidateId = `sha256:${"d".repeat(64)}`;
  const calls = [];
  const executor = async (_command, args) => {
    calls.push(args);
    if (args[0] === "image" && args[1] === "inspect") {
      const reference = args.at(-1);
      if (reference === "cmsify-candidate:test") return {
        exitCode: 0,
        stdout: JSON.stringify({
          Id: candidateId,
          Os: "linux",
          Architecture: "amd64",
          Config: { Labels: {
            "org.opencontainers.image.version": "1.0.0",
            "org.opencontainers.image.revision": candidateSourceSha,
          } },
        }),
        stderr: "",
        durationMs: 0,
      };
      return {
        exitCode: 0,
        stdout: JSON.stringify({ Os: "linux", Architecture: "amd64", RepoDigests: [reference] }),
        stderr: "",
        durationMs: 0,
      };
    }
    if (args.includes("pg_dump")) {
      controller.abort();
      throw new Error("raw tool abort detail token=secret");
    }
    return { exitCode: 0, stdout: "ok", stderr: "", durationMs: 0 };
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cancel-prerequisite-001",
      signal: controller.signal,
      dependencies: {
        createDockerHarness: (scope) => createDockerHarness(scope, executor),
        loadFixtureManifest: () => manifest,
        loadExpectedData: async () => ({ authentication: { readerToken: "cmsify_fixture-reader", adminPassword: "fixture-password" } }),
        verifyFixtureChecksums: async () => verifiedChecksums(),
      },
    }), (error) => {
      assert.equal(error.phase, "preflight");
      assert.match(error.message, /cancelled/i);
      assert.equal(error.message.includes("secret"), false);
      return true;
    });
    assert.equal(calls.some((args) => args[0] === "compose" && args.includes("up")), false);
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
    verifyPrerequisites: async () => ({ status: "passed" }),
    writeEnvironment: async () => events.push("environment:write"),
    up: async (services) => events.push(`up:${services.join(",")}`),
    stop: async (service) => events.push(`stop:${service}`),
    exec: async (service, args, options) => {
      events.push(`exec:${service}:${args[0]}`);
      if (args[0] === "assertion-probe") assert.equal(options.signal, controller.signal);
      return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    },
    copyTo: async (service, source) => events.push(`copy:${service}:${source.includes("backup") ? "backup" : "fixture"}`),
    discardDataVolumes: async (_options, finalFence) => {
      events.push("upgraded-volumes:resolve");
      await finalFence();
      events.push("upgraded-volumes:remove");
    },
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
        verifyFixtureChecksums: async () => verifiedChecksums(),
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
    assert.equal(events.filter((event) => event === "backup:verify-again").length, 2, "default discard must reverify again inside the destructive operation");
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
  assert.match(result.stderr, /rehearsal was cancelled/i);
  assert.equal(result.stderr.includes(secret), false);
  assert.equal(result.stderr.includes(repositoryRootForProcess), false);
  assert.equal(result.stderr.trim(), "Upgrade rehearsal was cancelled.");
});

test("CLI withholds arbitrary SQL, row, path, secret, and oversized failure text", () => {
  const script = `
    import { main } from "./eng/upgrade-tests/cli.mjs";
    const exitCode = await main([
      "rehearse", "--fixture", "tests/upgrade/fixtures/v0.1.3",
      "--candidate-image", "cmsify-candidate:test", "--candidate-version", "1.0.0",
      "--candidate-source-sha", "${candidateSourceSha}"
    ], {
      rehearse: async () => { throw new Error("SELECT password FROM users; row=user@example.test; token=raw-token; C:\\\\outside\\\\private.sql; " + "q".repeat(20_000)); },
    });
    process.exitCode = exitCode;
  `;
  const result = spawnSync(node, ["--input-type=module", "--eval", script], { cwd: repositoryRootForProcess, encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.equal(result.stderr.trim(), "Upgrade rehearsal failed; diagnostic detail withheld.");
  assert.ok(result.stderr.length <= 256);
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
  const snapshots = [];
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
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }), (error) => {
      assert.match(error.message, /candidate phase invariant failed/i);
      assert.equal(error.message.includes("cleanup-only-secret"), false);
      assert.ok(error.cause instanceof AggregateError);
      assert.match(error.cause.errors[0].message, /candidate phase invariant failed/i);
      assert.equal(error.cause.errors[0].phase, "candidate");
      assert.equal(error.cause.errors[1].message, "Owned-resource cleanup failed; diagnostic detail withheld.");
      assert.equal(error.cause.errors[1].code, "cleanup-failed");
      assert.equal(error.cause.errors[1].phase, "cleanup");
      return true;
    });

    const cleanup = snapshots.at(-1).phases.at(-1);
    assert.equal(cleanup.status, "failed");
    assert.equal(cleanup.errorCode, "cleanup-failed");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("cleanup-only failure agrees across persisted evidence and the public failure", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-cleanup-only-"));
  const snapshots = [];
  const operations = successfulOperations([]);
  operations.cleanup = async () => {
    throw new Error("cleanup password=hunter2 SELECT * FROM private_rows");
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cleanup-only-001",
      operations,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }), (error) => {
      assert.equal(error.phase, "cleanup");
      assert.equal(error.message, "Owned-resource cleanup failed; diagnostic detail withheld.");
      assert.equal(error.cause.code, "cleanup-failed");
      assert.equal(error.cause.phase, "cleanup");
      assert.equal(error.message.includes("hunter2"), false);
      return true;
    });

    const finalReport = snapshots.at(-1);
    const cleanup = finalReport.phases.at(-1);
    assert.equal(finalReport.status, "failed");
    assert.equal(cleanup.status, "failed");
    assert.equal(cleanup.errorCode, "cleanup-failed");
    assert.equal(cleanup.error, "Owned-resource cleanup failed; diagnostic detail withheld.");
    assert.equal(JSON.stringify(finalReport).includes("hunter2"), false);
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
    }), /report persistence failed/i);

    assert.deepEqual(events, ["diagnostics:capture", "owned-resources:cleanup"]);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("rejects candidate SemVer build metadata before direct or CLI rehearsal", async () => {
  assert.throws(() => validateCandidateInput({
    candidateImage: "cmsify-candidate:test",
    candidateVersion: "1.2.3+existing.build",
    candidateSourceSha,
  }), /build metadata/i);

  const cli = resolve(repositoryRootForProcess, "eng", "upgrade-tests", "cli.mjs");
  const result = spawnSync(node, [
    cli, "rehearse", "--fixture", "fixture",
    "--candidate-image", "cmsify-candidate:test", "--candidate-version", "1.2.3+existing.build",
    "--candidate-source-sha", candidateSourceSha,
  ], { cwd: repositoryRootForProcess, encoding: "utf8" });

  assert.equal(result.status, 1);
  assert.match(result.stderr, /build metadata/i);
  assert.equal(result.stderr.includes("+existing.build+"), false);
});

test("does not let a destination-only media inventory certify a matched backup", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-source-inventory-"));
  const scope = createRunScope(repositoryRoot, "source-inventory-001");
  const first = "first media";
  const second = "second media";
  const source = [
    { path: "cmsify/media/first.txt", body: first },
    { path: "cmsify/media/second.txt", body: second },
  ];
  let checksumIndex = 0;
  const harness = {
    exec: async (service, args) => {
      if (service === "minio" && args[0] === "mc" && args[1] === "ls") {
        return {
          exitCode: 0,
          stdout: `${source.map(({ path, body }) => JSON.stringify({ key: path, size: Buffer.byteLength(body), type: "file" })).join("\n")}\n`,
          stderr: "",
          durationMs: 0,
        };
      }
      if (service === "minio" && args[0] === "sha256sum") {
        const item = source[checksumIndex++];
        return { exitCode: 0, stdout: `${createHash("sha256").update(item.body).digest("hex")}  object\n`, stderr: "", durationMs: 0 };
      }
      return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    },
    copyFrom: async (service, _source, destination) => {
      if (service === "postgres") writeFileSync(destination, "database backup bytes");
      else {
        mkdirSync(resolve(destination, "cmsify", "media"), { recursive: true });
        writeFileSync(resolve(destination, "cmsify", "media", "first.txt"), first);
      }
    },
  };

  try {
    await assert.rejects(() => createMatchedBackup({
      harness,
      scope,
      baselineVersion: "0.1.3",
      now: () => "2026-08-27T12:00:00.000Z",
    }), /source.*inventory|media inventory/i);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("re-verifies the exact backup inside discard after its running transition", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-discard-fence-"));
  const events = [];
  const snapshots = [];
  let backupValid = true;
  let verifyCount = 0;
  const operations = successfulOperations(events);
  operations.backupReverify = async () => {
    events.push("backup:verify-again");
    verifyCount += 1;
  };
  operations.discardUpgradedState = async () => {
    events.push("backup:verify-inside-discard");
    verifyCount += 1;
    if (!backupValid) throw new Error("matched backup changed");
    events.push("upgraded-volumes:remove");
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "discard-fence-001",
      operations,
      reportWriter: async (report) => {
        snapshots.push(structuredClone(report));
        if (report.phases.find(({ name }) => name === "backup-reverify").status === "passed") backupValid = false;
      },
    }), /backup/i);

    assert.equal(verifyCount, 2);
    assert.equal(events.includes("upgraded-volumes:remove"), false);
    assert.equal(snapshots.at(-1).phases.find(({ name }) => name === "discard-upgraded-state").status, "failed");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("rolls back in-memory transitions when report persistence fails", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-report-transaction-"));
  const snapshots = [];
  const events = [];
  let writes = 0;

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "report-transaction-001",
      operations: successfulOperations(events),
      reportWriter: async (report) => {
        writes += 1;
        if (writes === 1) throw new Error("arbitrary persistence backend failure with password=hunter2");
        snapshots.push(structuredClone(report));
      },
    }), (error) => {
      assert.equal(error.phase, "preflight");
      assert.equal(error.message.includes("hunter2"), false);
      return true;
    });

    assert.deepEqual(events, ["diagnostics:capture", "owned-resources:cleanup"]);
    const finalReport = snapshots.at(-1);
    assert.equal(finalReport.phases[0].status, "failed");
    assert.notEqual(finalReport.phases[0].status, "running");
    assert.equal(finalReport.phases.at(-1).status, "passed");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("terminalizes cleanup when its passed report write fails transiently", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-cleanup-transaction-"));
  const snapshots = [];
  let failedOnce = false;

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cleanup-transaction-001",
      operations: successfulOperations([]),
      reportWriter: async (report) => {
        if (!failedOnce && report.phases.at(-1).status === "passed") {
          failedOnce = true;
          throw new Error("cleanup report backend leaked row=(secret)");
        }
        snapshots.push(structuredClone(report));
      },
    }), (error) => {
      assert.equal(error.phase, "cleanup");
      assert.match(error.message, /report persistence/i);
      assert.equal(error.message.includes("row=(secret)"), false);
      assert.equal(error.cause.code, "report-persistence-failed");
      assert.equal(error.cause.phase, "cleanup");
      return true;
    });

    const finalReport = snapshots.at(-1);
    assert.equal(finalReport.status, "failed");
    assert.equal(finalReport.phases.at(-1).status, "failed");
    assert.equal(finalReport.phases.at(-1).errorCode, "report-persistence-failed");
    assert.equal(finalReport.phases.some(({ status }) => status === "running"), false);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("classifies cleanup transition persistence separately from owned-resource cleanup", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-cleanup-boundary-"));
  const snapshots = [];
  const events = [];
  const operations = successfulOperations(events, "candidate");
  let failedCleanupTransition = false;

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "cleanup-boundary-001",
      operations,
      reportWriter: async (report) => {
        if (!failedCleanupTransition && report.phases.at(-1).status === "running") {
          failedCleanupTransition = true;
          throw new Error("report backend row=(secret)");
        }
        snapshots.push(structuredClone(report));
      },
    }), (error) => {
      assert.equal(error.phase, "candidate");
      assert.equal(error.cause instanceof AggregateError, true);
      assert.equal(error.cause.errors[1].code, "report-persistence-failed");
      assert.equal(error.cause.errors[1].phase, "cleanup");
      assert.equal(error.cause.errors[1].message.includes("row=(secret)"), false);
      return true;
    });

    assert.equal(events.at(-1), "owned-resources:cleanup");
    const cleanup = snapshots.at(-1).phases.at(-1);
    assert.equal(cleanup.status, "failed");
    assert.equal(cleanup.errorCode, "report-persistence-failed");
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("persists allow-listed partial evidence when an assertion phase fails", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-partial-evidence-"));
  const snapshots = [];
  const operations = successfulOperations([]);
  operations.baseline = async () => {
    const error = new Error("Invariant exact-migration-history failed: SELECT secret FROM rows");
    error.safeEvidence = {
      readiness: [{ service: "baseline-api", status: "ready", attempts: 3, path: "C:\\outside" }],
      assertions: [{ name: "exact-migration-history", status: "failed", rows: [{ password: "secret" }] }],
      sql: "SELECT secret FROM rows",
    };
    throw error;
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "partial-evidence-001",
      operations,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }), /baseline phase invariant/i);

    const evidence = snapshots.at(-1).phases.find(({ name }) => name === "baseline").evidence;
    assert.deepEqual(evidence, {
      readiness: [{ service: "baseline-api", status: "ready", attempts: 3 }],
      assertions: [{ name: "exact-migration-history", status: "failed" }],
    });
    assert.equal(JSON.stringify(evidence).includes("SELECT"), false);
    assert.equal(JSON.stringify(evidence).includes("outside"), false);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("keeps diagnostic-capture failure as bounded secondary evidence", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-diagnostic-secondary-"));
  const snapshots = [];
  const events = [];
  const operations = successfulOperations(events, "candidate");
  operations.captureDiagnostics = async () => {
    events.push("diagnostics:capture");
    throw new Error(`SELECT password FROM users; row=(token-secret); ${"x".repeat(20_000)}; C:\\outside\\secret.sql`);
  };

  try {
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "diagnostic-secondary-001",
      operations,
      reportWriter: async (report) => snapshots.push(structuredClone(report)),
    }), (error) => {
      assert.equal(error.phase, "candidate");
      assert.equal(error.cause instanceof AggregateError, true);
      assert.match(error.cause.errors[0].message, /candidate/i);
      assert.match(error.cause.errors[1].message, /diagnostic/i);
      assert.equal(error.cause.errors[1].message.includes("SELECT"), false);
      assert.ok(error.cause.errors[1].message.length <= 256);
      return true;
    });

    const serialized = JSON.stringify(snapshots.at(-1));
    for (const forbidden of ["SELECT", "row=(", "token-secret", "outside", "x".repeat(1_000)]) assert.equal(serialized.includes(forbidden), false);
    assert.deepEqual(snapshots.at(-1).diagnostics, { status: "failed", code: "diagnostic-capture-failed" });
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("persists bounded allow-listed assertion and readiness evidence", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-rehearsal-evidence-"));
  const events = [];
  const operations = successfulOperations(events);
  operations.baseline = async () => ({
    readiness: [{ service: "baseline-api", status: "ready", attempts: 2 }],
    assertions: [
      { name: "exact-migration-history", status: "passed", detail: "SELECT * FROM __EFMigrationsHistory; password=secret" },
      { name: "published-content", status: "passed", rows: [{ secret: "payload" }] },
    ],
  });

  try {
    const report = await rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "evidence-test-001",
      operations,
    });
    const evidence = report.phases.find(({ name }) => name === "baseline").evidence;
    assert.deepEqual(evidence.readiness, [{ service: "baseline-api", status: "ready", attempts: 2 }]);
    assert.deepEqual(evidence.assertions, [
      { name: "exact-migration-history", status: "passed" },
      { name: "published-content", status: "passed" },
    ]);
    assert.equal(JSON.stringify(evidence).includes("SELECT"), false);
    assert.ok(JSON.stringify(evidence).length <= 4_096);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
  }
});

test("rejects a linked report directory without writing outside the owned run", async (t) => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-report-link-"));
  const outside = mkdtempSync(resolve(tmpdir(), "cmsify-report-outside-"));
  const ownedParent = resolve(repositoryRoot, "artifacts", "upgrade-tests");
  mkdirSync(ownedParent, { recursive: true });
  try {
    try {
      symlinkSync(outside, resolve(ownedParent, "linked-report-001"), process.platform === "win32" ? "junction" : "dir");
    } catch (error) {
      if (["EPERM", "EACCES", "ENOTSUP"].includes(error.code)) return t.skip("filesystem does not permit link creation");
      throw error;
    }
    await assert.rejects(() => rehearse({
      repositoryRoot,
      fixtureDirectory: resolve(repositoryRoot, "fixture"),
      candidateImage: "cmsify-candidate:test",
      candidateVersion: "1.0.0",
      candidateSourceSha,
      runId: "linked-report-001",
      operations: successfulOperations([]),
    }), /report persistence failed/i);
    assert.equal(existsSync(resolve(outside, "report.json")), false);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
    rmSync(outside, { force: true, recursive: true });
  }
});

test("rejects a linked backup manifest leaf", async () => {
  const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-manifest-link-"));
  const outside = mkdtempSync(resolve(tmpdir(), "cmsify-manifest-outside-"));
  const scope = createRunScope(repositoryRoot, "linked-manifest-001");
  const backup = resolve(scope.diagnosticsDirectory, "backup");
  const media = resolve(backup, "media");
  const database = "database";
  const object = "media";
  mkdirSync(media, { recursive: true });
  writeFileSync(resolve(backup, "database.dump"), database);
  writeFileSync(resolve(media, "object.txt"), object);
  const mediaObjects = [{ path: "object.txt", size: Buffer.byteLength(object), sha256: createHash("sha256").update(object).digest("hex") }];
  const manifest = {
    schemaVersion: 1,
    runId: scope.runId,
    baselineVersion: "0.1.3",
    createdAt: "2026-08-27T12:00:00.000Z",
    databaseSha256: createHash("sha256").update(database).digest("hex"),
    sourceMediaObjectCount: 1,
    sourceMediaInventorySha256: createHash("sha256").update(JSON.stringify(mediaObjects)).digest("hex"),
    mediaObjects,
  };
  const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;
  const outsideManifest = resolve(outside, "manifest.json");
  writeFileSync(outsideManifest, manifestText);
  try {
    linkSync(outsideManifest, resolve(backup, "backup-manifest.json"));
    await assert.rejects(() => verifyMatchedBackup({
      scope,
      baselineVersion: "0.1.3",
      manifestSha256: createHash("sha256").update(manifestText).digest("hex"),
    }), /manifest is missing|linked|reparse/i);
  } finally {
    rmSync(repositoryRoot, { force: true, recursive: true });
    rmSync(outside, { force: true, recursive: true });
  }
});
