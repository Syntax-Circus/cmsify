import assert from "node:assert/strict";
import test from "node:test";

import { ProcessFailure, runProcess } from "../../../eng/upgrade-tests/process.mjs";

const node = process.execPath;
const timeoutMs = 5_000;
const evalScript = (script, ...args) => ["--eval", script, ...args];

test("passes metacharacters as one literal argument", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write(JSON.stringify(process.argv.slice(1)))", "x; Write-Output compromised"), { timeoutMs });

  assert.equal(JSON.parse(result.stdout)[0], "x; Write-Output compromised");
});

test("terminates a timed-out process", async () => {
  await assert.rejects(
    runProcess(node, evalScript("setTimeout(() => {}, 30_000)"), { timeoutMs: 50, phase: "timeout-test" }),
    (error) => error instanceof ProcessFailure && error.phase === "timeout-test: timeout" && error.durationMs < 5_000,
  );
});

test("terminates a process when its abort signal fires", async () => {
  const controller = new AbortController();
  const pending = runProcess(node, evalScript("setTimeout(() => {}, 30_000)"), {
    timeoutMs,
    signal: controller.signal,
    phase: "abort-test",
  });
  setTimeout(() => controller.abort(), 50);

  await assert.rejects(
    pending,
    (error) => error instanceof ProcessFailure && error.phase === "abort-test: aborted" && error.durationMs < 5_000,
  );
});

test("caps captured stdout and stderr at one MiB each", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write('o'.repeat(2 * 1024 * 1024)); process.stderr.write('e'.repeat(2 * 1024 * 1024))"), { timeoutMs });

  assert.ok(Buffer.byteLength(result.stdout) <= 1024 * 1024);
  assert.ok(Buffer.byteLength(result.stderr) <= 1024 * 1024);
});

test("reports a nonzero exit with a sanitized diagnostic tail", async () => {
  await assert.rejects(
    runProcess(node, evalScript("console.error('failure detail'); process.exit(7)"), { timeoutMs, phase: "nonzero-test" }),
    (error) => error instanceof ProcessFailure
      && error.exitCode === 7
      && error.message.includes("node")
      && error.message.includes("nonzero-test")
      && error.message.includes("7")
      && error.message.includes("failure detail"),
  );
});

test("reports a missing executable as a typed failure", async () => {
  await assert.rejects(
    runProcess("cmsify-command-that-does-not-exist", [], { timeoutMs, phase: "spawn-test" }),
    (error) => error instanceof ProcessFailure && error.phase === "spawn-test" && error.exitCode === null,
  );
});

test("redacts fixture and infrastructure secrets from results and failures", async () => {
  const secrets = {
    CMSIFY_FIXTURE_TOKEN: "fixture-token-secret",
    POSTGRES_PASSWORD: "postgres-password-secret",
    MINIO_ROOT_PASSWORD: "minio-password-secret",
    Secrets__EncryptionKey: "encryption-key-secret",
  };
  const command = "console.log(process.env.CMSIFY_FIXTURE_TOKEN, process.env.POSTGRES_PASSWORD); console.error(process.env.MINIO_ROOT_PASSWORD, process.env.Secrets__EncryptionKey); process.exit(9)";

  await assert.rejects(
    runProcess(node, evalScript(command), { timeoutMs, phase: "redaction-test", env: secrets }),
    (error) => {
      assert.ok(error instanceof ProcessFailure);
      for (const secret of Object.values(secrets)) assert.equal(error.message.includes(secret), false);
      assert.match(error.message, /<redacted>/);
      return true;
    },
  );
});
