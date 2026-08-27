import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { relative, resolve } from "node:path";
import test from "node:test";

import { createDockerHarness } from "../../../eng/upgrade-tests/docker.mjs";
import { createRunScope } from "../../../eng/upgrade-tests/paths.mjs";

const repositoryRoot = mkdtempSync(resolve(tmpdir(), "cmsify-upgrade-paths-"));

test.after(() => rmSync(repositoryRoot, { force: true, recursive: true }));

test("creates an isolated, repository-owned run scope", () => {
  const scope = createRunScope(repositoryRoot, "safe-run-001");

  assert.equal(scope.runId, "safe-run-001");
  assert.equal(scope.projectName, "safe-run-001");
  assert.equal(scope.diagnosticsDirectory, resolve(repositoryRoot, "artifacts", "upgrade-tests", scope.runId));
  assert.equal(relative(resolve(repositoryRoot, "artifacts", "upgrade-tests"), scope.diagnosticsDirectory).startsWith(".."), false);
  assert.deepEqual(scope.labels, {
    "io.syntaxcircus.cmsify.upgrade-test": "true",
    "io.syntaxcircus.cmsify.upgrade-run": scope.runId,
  });
});

test("generates a safe lower-case run id", () => {
  const scope = createRunScope(repositoryRoot);

  assert.match(scope.runId, /^cmsify-upgrade-[a-f0-9]{12}$/);
});

test("rejects diagnostics outside the repository-owned upgrade run root", () => {
  assert.throws(() => createRunScope(repositoryRoot, "..\\outside"), /safe run id/i);
});

test("rejects forged run scopes before they can create or delete an unowned env file", () => {
  const trusted = createRunScope(repositoryRoot, "safe-run-006");
  const outsideEnvironmentFile = resolve(repositoryRoot, "..", "unowned-upgrade.env");
  const forged = {
    ...trusted,
    runId: "..\\..\\unowned-upgrade",
    diagnosticsDirectory: resolve(repositoryRoot, "..", "unowned-diagnostics"),
    labels: {
      "io.syntaxcircus.cmsify.upgrade-test": "true",
      "io.syntaxcircus.cmsify.upgrade-run": "..\\..\\unowned-upgrade",
    },
  };

  assert.throws(() => createDockerHarness(forged), /trusted safe run scope/i);
  assert.equal(existsSync(outsideEnvironmentFile), false);
});

test("cleanup discovers resources using both exact ownership labels", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-002");
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };
  const harness = createDockerHarness(scope, executor);

  await harness.cleanup();

  const discoveryCalls = calls.filter(({ args }) => ["ps", "network", "volume"].includes(args[0]));
  assert.equal(discoveryCalls.length, 3);
  for (const { command, args } of discoveryCalls) {
    assert.equal(command, "docker");
    assert.deepEqual(args.filter((value) => value.startsWith("label=")), [
      "label=io.syntaxcircus.cmsify.upgrade-test=true",
      `label=io.syntaxcircus.cmsify.upgrade-run=${scope.runId}`,
    ]);
  }
});

test("compose commands carry the run scope without wildcard cleanup", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-003");
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };
  const harness = createDockerHarness(scope, executor);

  await harness.up(["postgres"]);
  await harness.stop("postgres");
  await harness.start("postgres");
  await harness.exec("postgres", ["pg_isready"]);
  await harness.logs();

  const composeCalls = calls.filter(({ args }) => args[0] === "compose");
  assert.equal(composeCalls.length, 5);
  for (const { command, args } of composeCalls) {
    assert.equal(command, "docker");
    assert.deepEqual(args.slice(0, 7), [
      "compose",
      "--project-name", scope.projectName,
      "--file", "tests/upgrade/compose.yml",
      "--env-file", resolve(repositoryRoot, "tests", "upgrade", ".runs", `${scope.runId}.env`),
    ]);
    assert.equal(args.includes("down"), false);
    assert.equal(args.includes("--volumes"), false);
  }
});

test("Docker exec forwards bounded cancellation and redaction options without changing its command boundary", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-010");
  const calls = [];
  const controller = new AbortController();
  const executor = async (command, args, options) => {
    calls.push({ command, args, options });
    return { exitCode: 0, stdout: Buffer.from("ok"), stderr: "", durationMs: 0 };
  };
  const harness = createDockerHarness(scope, executor);

  const result = await harness.exec("baseline-api", ["curl", "--version"], {
    timeoutMs: 5_000,
    signal: controller.signal,
    redact: ["fixture-secret"],
    stdoutEncoding: "buffer",
  });

  assert.deepEqual(result.stdout, Buffer.from("ok"));
  assert.deepEqual(calls[0].args.slice(-4), ["--no-TTY", "baseline-api", "curl", "--version"]);
  assert.equal(calls[0].options.timeoutMs, 5_000);
  assert.equal(calls[0].options.signal, controller.signal);
  assert.deepEqual(calls[0].options.redact, ["fixture-secret"]);
  assert.equal(calls[0].options.stdoutEncoding, "buffer");
});

test("Docker lifecycle commands forward the rehearsal cancellation boundary", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-015");
  const calls = [];
  const controller = new AbortController();
  const executor = async (command, args, options) => {
    calls.push({ command, args, options });
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };
  const harness = createDockerHarness(scope, executor);

  await harness.up(["postgres", "minio"], { signal: controller.signal, redact: ["fixture-secret"] });
  await harness.stop("postgres", { signal: controller.signal });
  await harness.copyTo("postgres", "fixture.sql", "/tmp/fixture.sql", { signal: controller.signal });

  assert.equal(calls.length, 3);
  assert.equal(calls.every(({ options }) => options.signal === controller.signal), true);
  assert.deepEqual(calls[0].options.redact, ["fixture-secret"]);
});

test("Docker exec opts into an interactive stdin pipe only when input is supplied", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-011");
  const calls = [];
  const executor = async (command, args, options) => {
    calls.push({ command, args, options });
    return { exitCode: 0, stdout: "1\n", stderr: "", durationMs: 0 };
  };
  const harness = createDockerHarness(scope, executor);

  await harness.exec("postgres", ["psql", "--file=-"], { stdin: "SELECT 1;" });

  assert.deepEqual(calls[0].args.slice(-5), ["--interactive", "--no-TTY", "postgres", "psql", "--file=-"]);
  assert.equal(calls[0].options.stdin, "SELECT 1;");
});

test("accepts Docker Hub's canonical repository spelling for an immutable digest", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-009");
  const digest = "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931";
  const executor = async () => ({
    exitCode: 0,
    stdout: JSON.stringify({ Os: "linux", Architecture: "amd64", RepoDigests: [`syntaxcircus/cmsify-api@${digest}`] }),
    stderr: "",
    durationMs: 0,
  });
  const harness = createDockerHarness(scope, executor);

  await harness.inspectImage({ repository: "docker.io/syntaxcircus/cmsify-api", digest, platform: "linux/amd64" });
});

test("inspects the candidate once and binds its stable ID, platform, and exact OCI identity", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-012");
  const sourceSha = "0123456789abcdef0123456789abcdef01234567";
  const imageId = `sha256:${"a".repeat(64)}`;
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    return {
      exitCode: 0,
      stdout: JSON.stringify({
        Id: imageId,
        Os: "linux",
        Architecture: "amd64",
        Config: {
          Labels: {
            "org.opencontainers.image.version": "1.0.0",
            "org.opencontainers.image.revision": sourceSha,
          },
        },
      }),
      stderr: "",
      durationMs: 0,
    };
  };

  const identity = await createDockerHarness(scope, executor).inspectCandidateImage("cmsify-candidate:test", {
    version: "1.0.0",
    sourceSha,
  });

  assert.deepEqual(identity, {
    reference: "cmsify-candidate:test",
    imageId,
    platform: "linux/amd64",
    version: "1.0.0",
    sourceSha,
    informationalVersion: `1.0.0+${sourceSha}`,
    labels: {
      "org.opencontainers.image.version": "1.0.0",
      "org.opencontainers.image.revision": sourceSha,
    },
  });
  assert.deepEqual(calls, [{ command: "docker", args: ["image", "inspect", "--format", "{{json .}}", "cmsify-candidate:test"] }]);
});

test("writes both candidate reference and inspected ID into the run-owned environment", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-013");
  const harness = createDockerHarness(scope, async () => ({ exitCode: 0, stdout: "", stderr: "", durationMs: 0 }));
  const imageId = `sha256:${"b".repeat(64)}`;

  await harness.writeEnvironment({
    CANDIDATE_API_IMAGE: imageId,
    CANDIDATE_API_IMAGE_REFERENCE: "cmsify-candidate:test",
    CANDIDATE_API_IMAGE_ID: imageId,
  });

  assert.equal(readFileSync(harness.environmentFile, "utf8"), [
    `CMSIFY_UPGRADE_RUN_ID=${scope.runId}`,
    "CMSIFY_UPGRADE_TEST_LABEL=true",
    `CANDIDATE_API_IMAGE=${imageId}`,
    "CANDIDATE_API_IMAGE_REFERENCE=cmsify-candidate:test",
    `CANDIDATE_API_IMAGE_ID=${imageId}`,
    "",
  ].join("\n"));
});

test("discarding upgraded state label-verifies exact data containers and volumes before removal", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-014");
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    if (args[0] === "compose" && args.includes("ps")) {
      return { exitCode: 0, stdout: "postgres-container\nminio-container\n", stderr: "", durationMs: 0 };
    }
    if (args.includes("inspect")) return { exitCode: 0, stdout: JSON.stringify(scope.labels), stderr: "", durationMs: 0 };
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };

  await createDockerHarness(scope, executor).discardDataVolumes();

  const removedContainers = calls.filter(({ args }) => args[0] === "rm").map(({ args }) => args.at(-1));
  const removedVolumes = calls.filter(({ args }) => args[0] === "volume" && args[1] === "rm").map(({ args }) => args.at(-1));
  assert.deepEqual(removedContainers, ["postgres-container", "minio-container"]);
  assert.deepEqual(removedVolumes, [`${scope.projectName}_postgres-data`, `${scope.projectName}_minio-data`]);
  const firstVolumeRemoval = calls.findIndex(({ args }) => args[0] === "volume" && args[1] === "rm");
  assert.ok(firstVolumeRemoval > calls.findLastIndex(({ args }) => args[0] === "rm"));
  assert.equal(calls.some(({ args }) => args.includes("prune") || args.includes("--volumes") || args.includes("down")), false);
});

test("captures logs before explicitly removing label-verified resources", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-004");
  const calls = [];
  const labels = JSON.stringify(scope.labels);
  const executor = async (command, args) => {
    calls.push({ command, args });
    if (args[0] === "ps") return { exitCode: 0, stdout: "container-id\n", stderr: "", durationMs: 0 };
    if (args[0] === "network" && args[1] === "ls") return { exitCode: 0, stdout: "network-id\n", stderr: "", durationMs: 0 };
    if (args[0] === "volume" && args[1] === "ls") return { exitCode: 0, stdout: "volume-id\n", stderr: "", durationMs: 0 };
    if (args.includes("inspect")) return { exitCode: 0, stdout: labels, stderr: "", durationMs: 0 };
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };

  await createDockerHarness(scope, executor).cleanup();

  const logsIndex = calls.findIndex(({ args }) => args.includes("logs"));
  const removalIndex = calls.findIndex(({ args }) => args[0] === "rm" || (args[0] === "network" && args[1] === "rm") || (args[0] === "volume" && args[1] === "rm"));
  assert.ok(logsIndex >= 0 && removalIndex > logsIndex);
  assert.ok(calls.some(({ args }) => args[0] === "network" && args[1] === "inspect" && args.includes("{{json .Labels}}")));
  assert.ok(calls.some(({ args }) => args[0] === "volume" && args[1] === "inspect" && args.includes("{{json .Labels}}")));
});

test("preserves a run environment file supplied by later orchestration", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-005");
  const executor = async () => ({ exitCode: 0, stdout: "", stderr: "", durationMs: 0 });
  const harness = createDockerHarness(scope, executor);

  await harness.up(["postgres"]);
  writeFileSync(harness.environmentFile, "CMSIFY_FIXTURE_TOKEN=fixture-only-value\n", "utf8");
  await harness.start("postgres");

  assert.equal(readFileSync(harness.environmentFile, "utf8"), "CMSIFY_FIXTURE_TOKEN=fixture-only-value\n");
});

test("continues owned cleanup when log collection fails", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-007");
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    if (args[0] === "compose" && args.includes("logs")) throw new Error("log collection failed");
    if (args[0] === "ps") return { exitCode: 0, stdout: "container-id\n", stderr: "", durationMs: 0 };
    if (args[0] === "network" && args[1] === "ls") return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    if (args[0] === "volume" && args[1] === "ls") return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    if (args.includes("inspect")) return { exitCode: 0, stdout: JSON.stringify(scope.labels), stderr: "", durationMs: 0 };
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };

  await assert.rejects(createDockerHarness(scope, executor).cleanup(), /log collection failed/);

  assert.ok(calls.some(({ args }) => args[0] === "ps"));
  assert.ok(calls.some(({ args }) => args[0] === "rm" && args.includes("container-id")));
});

test("refuses to remove a discovered resource without both ownership labels", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-008");
  const calls = [];
  const executor = async (command, args) => {
    calls.push({ command, args });
    if (args[0] === "ps") return { exitCode: 0, stdout: "container-id\n", stderr: "", durationMs: 0 };
    if (args[0] === "network" || args[0] === "volume") return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
    if (args.includes("inspect")) return {
      exitCode: 0,
      stdout: JSON.stringify({ "io.syntaxcircus.cmsify.upgrade-test": "true" }),
      stderr: "",
      durationMs: 0,
    };
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  };

  await assert.rejects(createDockerHarness(scope, executor).cleanup(), /lacks the required ownership labels/i);

  assert.equal(calls.some(({ args }) => args[0] === "rm" && args.includes("container-id")), false);
});

test("stores only a bounded structured Docker diagnostic summary", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-016");
  const secret = "cmsify_sensitive-token-value";
  const password = "fixture-password-value";
  const raw = `SELECT * FROM users; row=(1, '${secret}'); password=${password}; C:\\outside\\data.sql; ${"z".repeat(100_000)}`;
  const harness = createDockerHarness(scope, async (_command, args, options) => {
    assert.deepEqual(options.redact, [secret, password]);
    return { exitCode: 0, stdout: raw, stderr: raw, durationMs: 12 };
  });

  const summary = await harness.logs({ redact: [secret, password] });
  const artifact = resolve(scope.diagnosticsDirectory, "docker-diagnostics.json");
  const serialized = readFileSync(artifact, "utf8");

  assert.deepEqual(summary, { status: "captured", stdoutBytes: Buffer.byteLength(raw), stderrBytes: Buffer.byteLength(raw) });
  assert.ok(serialized.length <= 1_024);
  for (const forbidden of ["SELECT", "row=(", secret, password, "outside", "z".repeat(1_000)]) assert.equal(serialized.includes(forbidden), false);
  assert.equal(existsSync(resolve(scope.diagnosticsDirectory, "docker-compose.log")), false);
});

test("fails prerequisite probing before a Compose up when a required tool is missing", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-017");
  const calls = [];
  const harness = createDockerHarness(scope, async (command, args) => {
    calls.push({ command, args });
    if (args.includes("pg_restore")) throw new Error("missing tool");
    return { exitCode: 0, stdout: "ok", stderr: "", durationMs: 0 };
  });
  const image = (repository) => ({ repository, digest: `sha256:${"a".repeat(64)}`, platform: "linux/amd64" });

  await assert.rejects(() => harness.verifyPrerequisites({
    postgresImage: image("postgres"),
    minioImage: image("minio"),
    baselineApiImage: image("baseline-api"),
    candidateImageId: `sha256:${"b".repeat(64)}`,
  }), /prerequisite/i);

  assert.equal(calls.some(({ args }) => args[0] === "compose" && args.includes("up")), false);
  assert.equal(existsSync(harness.environmentFile), false);
});

test("records unavailable diagnostics without Compose or an env write before resources start", async () => {
  const scope = createRunScope(repositoryRoot, "safe-run-019");
  const calls = [];
  const harness = createDockerHarness(scope, async (command, args) => {
    calls.push({ command, args });
    return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
  });

  const summary = await harness.logs({ resourcesStarted: false, redact: ["fixture-secret"] });

  assert.deepEqual(summary, { status: "unavailable", stdoutBytes: 0, stderrBytes: 0 });
  assert.deepEqual(calls, []);
  assert.equal(existsSync(harness.environmentFile), false);
});

test("rejects a junction parent for the run environment file", async (t) => {
  const root = mkdtempSync(resolve(tmpdir(), "cmsify-env-link-"));
  const outside = mkdtempSync(resolve(tmpdir(), "cmsify-env-outside-"));
  mkdirSync(resolve(root, "tests", "upgrade"), { recursive: true });
  try {
    try {
      symlinkSync(outside, resolve(root, "tests", "upgrade", ".runs"), process.platform === "win32" ? "junction" : "dir");
    } catch (error) {
      if (["EPERM", "EACCES", "ENOTSUP"].includes(error.code)) return t.skip("filesystem does not permit link creation");
      throw error;
    }
    const harness = createDockerHarness(createRunScope(root, "safe-run-018"), async () => ({ exitCode: 0, stdout: "", stderr: "", durationMs: 0 }));
    await assert.rejects(() => harness.writeEnvironment({ VALUE: "safe" }), /linked|reparse|safe.*path/i);
    assert.equal(existsSync(resolve(outside, "safe-run-018.env")), false);
  } finally {
    rmSync(root, { force: true, recursive: true });
    rmSync(outside, { force: true, recursive: true });
  }
});
