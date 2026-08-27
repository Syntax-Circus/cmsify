import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
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
