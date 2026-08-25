import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const verifier = resolve(repositoryRoot, "scripts", "release", "verify-release-artifacts.mjs");

test("fails closed when a candidate is missing its archives, SBOMs, checksums, and OCI layouts", () => {
  const artifacts = mkdtempSync(resolve(tmpdir(), "cmsify-release-artifacts-"));
  try {
    const result = spawnSync(process.execPath, [verifier, "--artifacts", artifacts, "--version", "1.0.0", "--source-sha", "0123456789abcdef0123456789abcdef01234567"], { encoding: "utf8" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /NuGet|npm|OCI|SBOM|checksum/i);
  } finally {
    rmSync(artifacts, { recursive: true, force: true });
  }
});

test("rejects a candidate with an invalid source SHA before artifact inspection", () => {
  const artifacts = mkdtempSync(resolve(tmpdir(), "cmsify-release-artifacts-"));
  try {
    const result = spawnSync(process.execPath, [verifier, "--artifacts", artifacts, "--version", "1.0.0", "--source-sha", "not-a-commit"], { encoding: "utf8" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /immutable 40-character source SHA/i);
  } finally {
    rmSync(artifacts, { recursive: true, force: true });
  }
});

test("rejects an invalid SemVer candidate before artifact inspection", () => {
  const artifacts = mkdtempSync(resolve(tmpdir(), "cmsify-release-artifacts-"));
  try {
    const result = spawnSync(process.execPath, [verifier, "--artifacts", artifacts, "--version", "1.0", "--source-sha", "0123456789abcdef0123456789abcdef01234567"], { encoding: "utf8" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /requires a SemVer version/i);
  } finally {
    rmSync(artifacts, { recursive: true, force: true });
  }
});
