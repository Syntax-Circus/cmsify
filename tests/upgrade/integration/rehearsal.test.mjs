import assert from "node:assert/strict";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { rehearse } from "../../../eng/upgrade-tests/rehearsal.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");

function optionsFromEnvironment() {
  const candidateImage = process.env.CMSIFY_UPGRADE_CANDIDATE_IMAGE;
  const candidateVersion = process.env.CMSIFY_UPGRADE_CANDIDATE_VERSION;
  const candidateSourceSha = process.env.CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA;
  assert.ok(candidateImage, "CMSIFY_UPGRADE_CANDIDATE_IMAGE is required");
  assert.ok(candidateVersion, "CMSIFY_UPGRADE_CANDIDATE_VERSION is required");
  assert.ok(candidateSourceSha, "CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA is required");
  return {
    repositoryRoot,
    fixtureDirectory: resolve(repositoryRoot, "tests", "upgrade", "fixtures", "v0.1.3"),
    candidateImage,
    candidateVersion,
    candidateSourceSha,
  };
}

test("published v0.1.3 upgrades to the candidate and restores rollback", {
  skip: process.env.CMSIFY_UPGRADE_TEST !== "1",
}, async () => {
  const first = await rehearse(optionsFromEnvironment());
  const second = await rehearse(optionsFromEnvironment());
  assert.equal(first.result, "passed");
  assert.equal(second.result, "passed");
  assert.equal(first.fixtureDigest, second.fixtureDigest);
});
