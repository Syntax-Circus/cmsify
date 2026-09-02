import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const workflowPath = path.join(root, ".github/workflows/record-release-soak.yml");

test("dedicated soak recorder authenticates an elapsed release interval and attests exact evidence", () => {
  assert.equal(existsSync(workflowPath), true, "dedicated soak recorder workflow is missing");
  const workflow = readFileSync(workflowPath, "utf8");

  assert.match(workflow, /^on:\s*\n\s+workflow_dispatch:\s*\n\s+inputs:/m);
  assert.match(workflow, /release_run_id:[\s\S]*required:\s*true[\s\S]*release_tag:[\s\S]*required:\s*true/);
  assert.match(workflow, /permissions:\s*\n\s+contents:\s*read\s*\n\s+actions:\s*read\s*\n\s+attestations:\s*write\s*\n\s+id-token:\s*write/);
  assert.match(workflow, /\[\[ "\$GITHUB_REF" == "refs\/heads\/main" \]\]/);
  assert.doesNotMatch(workflow, /^\s+if:\s*github\.ref/m, "wrong-ref dispatches must fail rather than silently skip the recorder job");
  assert.match(workflow, /timeout-minutes:\s*10/);

  assert.match(workflow, /actions\/runs\/\$RELEASE_RUN_ID/);
  assert.match(workflow, /\.head_repository\.full_name == \$repository/);
  assert.match(workflow, /\.repository\.full_name == \$repository/);
  assert.doesNotMatch(workflow, /workflow_ref/, "Actions REST run identity must not depend on the unavailable workflow_ref runtime field");
  assert.match(workflow, /artifact-smoke/);
  assert.match(workflow, /upgrade-rollback/);
  assert.match(workflow, /SOAK_SECONDS[\s\S]*-lt 3600/);
  assert.doesNotMatch(workflow, /\bsleep\b/);

  for (const field of ["schema", "releaseRunId", "releaseTag", "sourceSha", "smokeJobId", "upgradeRollbackJobId", "soakRecorderRunId", "soakRecorderSourceSha", "smokePassed", "upgradeRollbackPassed", "passed", "startedAtUtc", "completedAtUtc"]) {
    assert.match(workflow, new RegExp(`--arg(?:json)? ${field}\\b`), `missing exact ${field} evidence field`);
  }
  assert.match(workflow, /cmsify\.hosted-soak-evidence\.v1/);
  assert.match(workflow, /actions\/upload-artifact@[0-9a-f]{40}/);
  assert.match(workflow, /actions\/attest-build-provenance@[0-9a-f]{40}/);
  assert.match(workflow, /subject-path:\s*soak-evidence\/cmsify-hosted-soak\.json/);
});
