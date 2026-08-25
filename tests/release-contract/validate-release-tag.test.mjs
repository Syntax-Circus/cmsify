import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const validator = resolve(repositoryRoot, "scripts", "release", "validate-release-tag.mjs");
const sourceSha = "0123456789abcdef0123456789abcdef01234567";

function validate(tag) {
  return spawnSync(process.execPath, [validator, tag, "--source-sha", sourceSha], { cwd: repositoryRoot, encoding: "utf8" });
}

test("accepts stable and prerelease v1 SemVer tags", () => {
  for (const tag of ["v1.0.0", "v1.0.0-rc.1"]) {
    const result = validate(tag);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), tag.slice(1));
  }
});

test("fails closed for a later 0.x tag without its checked-in upgrade fixture manifest", () => {
  const result = validate("v0.1.4");
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /upgrade fixture manifest/i);
});

test("does not require a future-fixture manifest for the current published 0.1.3 baseline", () => {
  const result = validate("v0.1.3");
  assert.equal(result.status, 0, result.stderr);
});
