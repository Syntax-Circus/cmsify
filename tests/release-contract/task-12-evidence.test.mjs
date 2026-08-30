import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const evidencePath = path.join(root, "docs/evidence/task-12-local-verification.json");
const sha = "70afef5c647f53944fd61d1b35f40ece940aacf7";
const docs = ["docs/v1-release-readiness.md", "docs/v1-release-remediation-handoff.md", "docs/superpowers/plans/2026-08-24-v1-remediation.md"].map((file) => readFileSync(path.join(root, file), "utf8")).join("\n");
const outerPlan = readFileSync(path.join(root, "docs/superpowers/plans/2026-08-24-v1-remediation.md"), "utf8");
const gateKeys = ["publicPackageRestore", "hostedAccessibility", "protectedApprovals", "artifactAttestation", "registrySigning", "immutableOciPromotion", "hostedSmokeSoak", "finalRelease"];
const commandInputs = ["CMSIFY_RELEASE_RUN_ID", "CMSIFY_CHECKSUMS_PATH", "CMSIFY_API_DIGEST", "CMSIFY_ADMIN_DIGEST", "CMSIFY_RELEASE_VERSION", "CMSIFY_RELEASE_TAG", "CMSIFY_WORKFLOW_IDENTITY", "CMSIFY_ACCESSIBILITY_JOB_ID", "CMSIFY_PROMOTE_JOB_ID", "CMSIFY_SMOKE_JOB_ID", "CMSIFY_UPGRADE_ROLLBACK_JOB_ID"];

function load() { assert.ok(existsSync(evidencePath), "Task 12 evidence manifest must exist"); return JSON.parse(readFileSync(evidencePath, "utf8")); }
function task12Section(plan) { return plan.match(/### Task 12:[\s\S]*?(?=\n## Completion Gate)/)?.[0] ?? ""; }
function validate(evidence, documentText = docs, planText = outerPlan) {
  assert.equal(evidence.schema, "cmsify.task12-evidence.v1");
  for (const key of ["sourceSha", "sdkVersion", "nodeVersion", "dockerClientVersion", "dockerServerVersion", "localFeedPackage", "checks", "artifacts", "externalGates", "knownDiagnostics"]) assert.ok(key in evidence, `missing ${key}`);
  assert.equal(evidence.sourceSha, sha, "evidence must not transplant a stale source SHA");
  assert.equal(evidence.sdkVersion, "10.0.400"); assert.equal(evidence.nodeVersion, "v24.14.1"); assert.equal(evidence.dockerClientVersion, "29.7.2"); assert.equal(evidence.dockerServerVersion, null);
  assert.deepEqual(evidence.localFeedPackage, { id: "SyntaxCircus.Http.Resilience", version: "0.2.0-cmsify.1", sha256: "17843D8C0A3422FCE37A3CEAC38029C638B099F01F044B09F30AD237D1786A1C", publicRestoreValidated: false });
  assert.deepEqual(evidence.checks, [{ name: "release-contract suite", command: "node --test tests/release-contract/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 328, passed: 328, failed: 0 }, sourceSha: sha }, { name: "Task 8 completion gate", command: "dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1", exitCode: null, status: "notRun", counts: null, passed: false, reason: "Not run at this evidence revision; Task 8 must refresh this manifest after its fixes and full gate.", sourceSha: sha }]);
  assert.deepEqual(evidence.artifacts, [{ kind: "OCI and package candidates", status: "absent/local-unpublished", digest: null }]);
  assert.deepEqual(Object.keys(evidence.commandInputs).sort(), commandInputs.sort());
  for (const input of commandInputs) assert.deepEqual(evidence.commandInputs[input], { value: null, reason: "Unperformed external gate; release operator must supply the immutable value.", owner: "release operator" });
  assert.deepEqual(Object.keys(evidence.externalGates).sort(), [...gateKeys].sort(), "external gate set must be exact");
  const owners = { publicPackageRestore: "release operator", hostedAccessibility: "release operator", protectedApprovals: "approver", artifactAttestation: "release operator", registrySigning: "release operator", immutableOciPromotion: "release operator", hostedSmokeSoak: "release operator", finalRelease: "approver" };
  const commands = { publicPackageRestore: "dotnet restore Cmsify.slnx --locked-mode", hostedAccessibility: "gh run view $env:CMSIFY_RELEASE_RUN_ID --repo Syntax-Circus/cmsify --job $env:CMSIFY_ACCESSIBILITY_JOB_ID --log --exit-status", protectedApprovals: "gh run view $env:CMSIFY_RELEASE_RUN_ID --repo Syntax-Circus/cmsify --job $env:CMSIFY_PROMOTE_JOB_ID --log --exit-status", artifactAttestation: "gh attestation verify $env:CMSIFY_CHECKSUMS_PATH --repo Syntax-Circus/cmsify", registrySigning: "cosign verify --certificate-identity $env:CMSIFY_WORKFLOW_IDENTITY --certificate-oidc-issuer https://token.actions.githubusercontent.com docker.io/syntaxcircus/cmsify-api@$env:CMSIFY_API_DIGEST; if ($LASTEXITCODE) { throw 'API signature verification failed' }; cosign verify --certificate-identity $env:CMSIFY_WORKFLOW_IDENTITY --certificate-oidc-issuer https://token.actions.githubusercontent.com docker.io/syntaxcircus/cmsify-admin@$env:CMSIFY_ADMIN_DIGEST; if ($LASTEXITCODE) { throw 'Admin signature verification failed' }", immutableOciPromotion: "$api=(oras manifest fetch --descriptor docker.io/syntaxcircus/cmsify-api:$env:CMSIFY_RELEASE_VERSION|ConvertFrom-Json).digest; if ($api -ne $env:CMSIFY_API_DIGEST) { throw 'API digest mismatch' }; $admin=(oras manifest fetch --descriptor docker.io/syntaxcircus/cmsify-admin:$env:CMSIFY_RELEASE_VERSION|ConvertFrom-Json).digest; if ($admin -ne $env:CMSIFY_ADMIN_DIGEST) { throw 'Admin digest mismatch' }", hostedSmokeSoak: "gh run view $env:CMSIFY_RELEASE_RUN_ID --repo Syntax-Circus/cmsify --job $env:CMSIFY_SMOKE_JOB_ID --log --exit-status; if ($LASTEXITCODE) { throw 'Smoke job failed' }; gh run view $env:CMSIFY_RELEASE_RUN_ID --repo Syntax-Circus/cmsify --job $env:CMSIFY_UPGRADE_ROLLBACK_JOB_ID --log --exit-status; if ($LASTEXITCODE) { throw 'Upgrade rollback job failed' }", finalRelease: "gh release view $env:CMSIFY_RELEASE_TAG --repo Syntax-Circus/cmsify --json name,tagName,url" };
  for (const name of gateKeys) { const gate = evidence.externalGates[name]; assert.equal(gate.passed, false, `${name} must remain false`); assert.ok(gate.reason?.trim()); assert.equal(gate.owner, owners[name]); assert.equal(gate.nextCommand, commands[name]); assert.equal(gate.evidenceLink, null); assert.doesNotMatch(gate.nextCommand, /<[^>]+>/); for (const variable of gate.nextCommand.matchAll(/\$env:([A-Z0-9_]+)/g)) assert.ok(commandInputs.includes(variable[1])); }
  assert.ok(evidence.knownDiagnostics.some((item) => /unrecognized contract step sequence/i.test(item) && /Task 8/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /concessive hosted-claim exemption/i.test(item) && /Task 8/i.test(item)));
  assert.match(documentText, /task-12-local-verification\.json/);
  assert.match(documentText, /not ready/i);
  for (const claim of [/public(?:\/CI)? restore (?:passed|succeeded|validated|green|certified)/i, /hosted (?:validation|checks|workflow) (?:passed|succeeded|validated|green|certified)/i, /repository implementation complete/i, /v1 certified/i, /release[- ]ready/i]) assert.ok(!documentText.split(/\r?\n/).some((line) => claim.test(line)));
  const section = task12Section(planText); assert.ok(section); assert.ok(/^\s*- \[ \]/m.test(section)); assert.doesNotMatch(section, /^\s*- \[x\]/im);
}
test("Task 12 evidence is complete, SHA-bound, and honest", () => validate(load()));
test("Task 12 evidence mutations are rejected", () => {
  const evidence = load();
  for (const mutate of [
    (copy) => { delete copy.checks[0].command; },
    (copy) => { delete copy.checks[0].counts; },
    (copy) => { copy.checks = []; }, (copy) => { copy.artifacts = []; },
    (copy) => { copy.sdkVersion = "0"; }, (copy) => { copy.nodeVersion = "v0"; }, (copy) => { copy.dockerClientVersion = "0"; }, (copy) => { copy.dockerServerVersion = "0"; },
    (copy) => { copy.checks[0].command = "stale"; }, (copy) => { copy.checks[0].counts.total = 0; }, (copy) => { copy.checks[1].reason = " "; },
    (copy) => { copy.localFeedPackage.sha256 = "bad"; },
    (copy) => { copy.localFeedPackage.publicRestoreValidated = true; },
    (copy) => { copy.artifacts[0].status = "local-published"; }, (copy) => { copy.artifacts[0].status = "promotion-complete"; },
    (copy) => { copy.sourceSha = "0".repeat(40); },
    (copy) => { copy.externalGates.publicPackageRestore.passed = true; },
    (copy) => { delete copy.externalGates.hostedAccessibility; },
    (copy) => { delete copy.externalGates.registrySigning.reason; },
    (copy) => { delete copy.commandInputs.CMSIFY_API_DIGEST; },
    ...gateKeys.flatMap((key) => [(copy) => { copy.externalGates[key].owner = "repository administrator"; }, (copy) => { copy.externalGates[key].nextCommand = "echo changed"; }, (copy) => { delete copy.externalGates[key].owner; }, (copy) => { copy.externalGates[key].reason = ""; }, (copy) => { copy.externalGates[key].nextCommand = ""; }, (copy) => { copy.externalGates[key].evidenceLink = "x"; }, (copy) => { copy.externalGates[key].passed = true; }]),
    (copy) => { delete copy.externalGates.finalRelease.evidenceLink; },
    (copy) => { delete copy.checks[1].status; },
  ]) { const copy = structuredClone(evidence); const before = JSON.stringify(copy); mutate(copy); assert.notEqual(JSON.stringify(copy), before); assert.throws(() => validate(copy)); }
  for (const claim of ["Public restore passed, but not yet.", "Hosted validation succeeded although not final.", "Repository implementation complete.", "v1 certified.", "Release-ready."]) { const changed = `${docs}\n${claim}`; assert.notEqual(changed, docs); assert.throws(() => validate(evidence, changed)); }
  const section = task12Section(outerPlan); for (const checkbox of [...section.matchAll(/- \[ \]/g)]) { const position = checkbox.index; const changedSection = `${section.slice(0, position)}- [x]${section.slice(position + 5)}`; const changedPlan = outerPlan.replace(section, changedSection); assert.notEqual(changedPlan, outerPlan); assert.throws(() => validate(evidence, docs, changedPlan)); }
});
