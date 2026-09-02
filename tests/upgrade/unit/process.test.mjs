import assert from "node:assert/strict";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";

import { ProcessFailure, runProcess } from "../../../eng/upgrade-tests/process.mjs";

const node = process.execPath;
const timeoutMs = 5_000;
const evalScript = (script, ...args) => ["--eval", script, ...args];
const temporaryDirectory = mkdtempSync(resolve(tmpdir(), "cmsify-upgrade-process-"));

test.after(() => rmSync(temporaryDirectory, { force: true, recursive: true }));

const wait = (milliseconds) => new Promise((resolve_) => setTimeout(resolve_, milliseconds));

function processWithDelayedDescendant(marker) {
  const childScript = `setTimeout(() => require("node:fs").writeFileSync(${JSON.stringify(marker)}, "survived"), 400)`;
  return evalScript(`require("node:child_process").spawn(process.execPath, ["--eval", ${JSON.stringify(childScript)}], { stdio: "ignore" }); setTimeout(() => {}, 30_000)`);
}

test("passes metacharacters as one literal argument", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write(JSON.stringify(process.argv.slice(1)))", "x; Write-Output compromised"), { timeoutMs });

  assert.equal(JSON.parse(result.stdout)[0], "x; Write-Output compromised");
});

test("returns exact stdout bytes when the caller requests a binary result", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write(Buffer.from([0x7b, 0xff, 0x7d]))"), {
    timeoutMs,
    stdoutEncoding: "buffer",
  });

  assert.deepEqual(result.stdout, Buffer.from([0x7b, 0xff, 0x7d]));
  assert.equal(typeof result.stderr, "string");
});

test("passes a constant input string through standard input without a shell", async () => {
  const statement = "SELECT :'fixture_value';";
  const result = await runProcess(node, evalScript("process.stdin.pipe(process.stdout)"), {
    timeoutMs,
    stdin: statement,
  });

  assert.equal(result.stdout, statement);
});

test("passes bounded binary input through standard input without text conversion", async () => {
  const archive = Buffer.from([0x50, 0x47, 0x44, 0x4d, 0x50, 0x00, 0xff]);
  const result = await runProcess(node, evalScript("process.stdin.pipe(process.stdout)"), {
    timeoutMs,
    stdin: archive,
    stdoutEncoding: "buffer",
  });

  assert.deepEqual(result.stdout, archive);
});

test("terminates a timed-out process", async () => {
  const marker = resolve(temporaryDirectory, "timeout-descendant-marker");
  await assert.rejects(
    runProcess(node, processWithDelayedDescendant(marker), { timeoutMs: 50, phase: "timeout-test" }),
    (error) => error instanceof ProcessFailure && error.phase === "timeout-test: timeout" && error.durationMs < 5_000,
  );
  await wait(700);
  assert.equal(existsSync(marker), false, "timeout must terminate descendants as well as the direct child");
});

test("terminates a process when its abort signal fires", async () => {
  const controller = new AbortController();
  const marker = resolve(temporaryDirectory, "abort-descendant-marker");
  const pending = runProcess(node, processWithDelayedDescendant(marker), {
    timeoutMs,
    signal: controller.signal,
    phase: "abort-test",
  });
  setTimeout(() => controller.abort(), 50);

  await assert.rejects(
    pending,
    (error) => error instanceof ProcessFailure && error.phase === "abort-test: aborted" && error.durationMs < 5_000,
  );
  await wait(700);
  assert.equal(existsSync(marker), false, "abort must terminate descendants as well as the direct child");
});

test("caps captured stdout and stderr at one MiB each", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write('o'.repeat(2 * 1024 * 1024)); process.stderr.write('e'.repeat(2 * 1024 * 1024))"), { timeoutMs });

  assert.ok(Buffer.byteLength(result.stdout) <= 1024 * 1024);
  assert.ok(Buffer.byteLength(result.stderr) <= 1024 * 1024);
});

test("caps output after redaction expands short secrets", async () => {
  const result = await runProcess(node, evalScript("process.stdout.write(process.env.CMSIFY_FIXTURE_TOKEN.repeat(1024 * 1024))"), {
    timeoutMs,
    env: { CMSIFY_FIXTURE_TOKEN: "x" },
  });

  assert.ok(Buffer.byteLength(result.stdout) <= 1024 * 1024);
  assert.equal(result.stdout.includes("x"), false);
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
