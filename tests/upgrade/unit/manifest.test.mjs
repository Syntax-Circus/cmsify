import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";

import { REQUIRED_SCENARIOS, validateFixtureManifest } from "../../../eng/upgrade-tests/manifest.mjs";

const fixtureDirectory = mkdtempSync(resolve(tmpdir(), "cmsify-upgrade-manifest-"));

test.after(() => rmSync(fixtureDirectory, { force: true, recursive: true }));

function validManifest() {
  return {
    schemaVersion: 1,
    baseline: {
      version: "0.1.3",
      sourceSha: "bc652aec1acad7ef440576b5019a0fe7c72004b3",
      apiImage: {
        repository: "docker.io/syntaxcircus/cmsify-api",
        tag: "0.1.3",
        digest: "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931",
        platform: "linux/amd64",
      },
      postgresImage: {
        repository: "docker.io/library/postgres",
        tag: "17-alpine",
        digest: "sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee",
        platform: "linux/amd64",
      },
      minioImage: {
        repository: "docker.io/minio/minio",
        tag: "RELEASE.2025-09-07T16-13-09Z",
        digest: "sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2",
        platform: "linux/amd64",
      },
    },
    requiredFiles: ["database.sql", "expected.json", "manifest.json", "media/sample.txt"],
    requiredScenarios: [...REQUIRED_SCENARIOS],
    expectedDataFile: "expected.json",
  };
}

function absoluteFile(manifest) {
  manifest.requiredFiles[0] = "/database.sql";
  return manifest;
}

function escapingFile(manifest) {
  manifest.requiredFiles[0] = "../database.sql";
  return manifest;
}

function tagDigestMismatch(manifest) {
  manifest.baseline.apiImage.tag = "0.1.4";
  return manifest;
}

function missingScenario(manifest) {
  manifest.requiredScenarios.pop();
  return manifest;
}

function duplicateFile(manifest) {
  manifest.requiredFiles[3] = "expected.json";
  return manifest;
}

function unknownSchema(manifest) {
  manifest.schemaVersion = 2;
  return manifest;
}

function repeatedSeparator(manifest) {
  manifest.requiredFiles[3] = "media//sample.txt";
  return manifest;
}

test("accepts the immutable v0.1.3 fixture contract", () => {
  const manifest = validateFixtureManifest(validManifest(), fixtureDirectory);

  assert.equal(manifest.baseline.version, "0.1.3");
  assert.equal(manifest.baseline.sourceSha, "bc652aec1acad7ef440576b5019a0fe7c72004b3");
  assert.equal(manifest.baseline.apiImage.digest, "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931");
  assert.equal(manifest.baseline.apiImage.platform, "linux/amd64");
  assert.deepEqual(manifest.requiredFiles, ["database.sql", "expected.json", "manifest.json", "media/sample.txt"]);
  assert.deepEqual(new Set(manifest.requiredScenarios), REQUIRED_SCENARIOS);
});

for (const mutate of [absoluteFile, escapingFile, tagDigestMismatch, missingScenario, duplicateFile, unknownSchema, repeatedSeparator]) {
  test(`rejects ${mutate.name}`, () => {
    assert.throws(() => validateFixtureManifest(mutate(validManifest()), fixtureDirectory));
  });
}
