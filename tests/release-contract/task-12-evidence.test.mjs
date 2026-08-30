import assert from "node:assert/strict";
import { createHash } from "node:crypto";
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
const commandInputs = ["CMSIFY_RELEASE_RUN_ID", "CMSIFY_CHECKSUMS_PATH", "CMSIFY_API_DIGEST", "CMSIFY_ADMIN_DIGEST", "CMSIFY_RELEASE_VERSION", "CMSIFY_RELEASE_TAG", "CMSIFY_COSIGN_CERTIFICATE_IDENTITY", "CMSIFY_ATTESTATION_SIGNER_WORKFLOW", "CMSIFY_ACCESSIBILITY_JOB_ID", "CMSIFY_PROMOTE_JOB_ID", "CMSIFY_SMOKE_JOB_ID", "CMSIFY_UPGRADE_ROLLBACK_JOB_ID", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_SOAK_EVIDENCE_PATH", "CMSIFY_SOAK_EVIDENCE_SHA256"];
const commandHashes = { publicPackageRestore: "8c5f197769a0dfb1827c8c8e591241a84b4baf5e1b86cd5ba69ea557dba90802", hostedAccessibility: "46595d036b67d564535df78fe855d955a6334ae09011143ac9cac402cb71b87a", protectedApprovals: "82015b66e81fb44874922df638a226dca0aefb3f24cc6ed758fdd4ee5c7bf6b1", artifactAttestation: "97edc3457d53334c86ba3d7093d7db95d613067a1c0448cf699a7611158f36bd", registrySigning: "f07c8b1ff5e827bcf356bb261e48e29f35922f02e15fceb174d44d8a2ca628c3", immutableOciPromotion: "791ba0b7d42776b4f02520cd0bfdcd67560a2205379e5092443ee399309f7f22", hostedSmokeSoak: "99670c79701613787772dc0b7b5626886ecaf4d14c1e87c60fd2b7d121d85022", finalRelease: "fb57f80e7a61ee08f4ca5eccff12c42fd16b846a9a69884fc8d8d8db2c2f529f" };
const digest = (value) => createHash("sha256").update(value).digest("hex");

function load() { assert.ok(existsSync(evidencePath), "Task 12 evidence manifest must exist"); return JSON.parse(readFileSync(evidencePath, "utf8")); }
function task12Section(plan) { return plan.match(/### Task 12:[\s\S]*?(?=\n## Completion Gate)/)?.[0] ?? ""; }
function validate(evidence, documentText = docs, planText = outerPlan) {
  assert.equal(evidence.schema, "cmsify.task12-evidence.v1");
  for (const key of ["sourceSha", "sdkVersion", "nodeVersion", "dockerClientVersion", "dockerServerVersion", "localFeedPackage", "checks", "artifacts", "externalGates", "knownDiagnostics"]) assert.ok(key in evidence, `missing ${key}`);
  assert.equal(evidence.sourceSha, sha, "evidence must not transplant a stale source SHA");
  assert.equal(evidence.sdkVersion, "10.0.400"); assert.equal(evidence.nodeVersion, "v24.14.1"); assert.equal(evidence.dockerClientVersion, "29.7.2"); assert.equal(evidence.dockerServerVersion, null);
  assert.deepEqual(evidence.localFeedPackage, { id: "SyntaxCircus.Http.Resilience", version: "0.2.0-cmsify.1", sha256: "17843D8C0A3422FCE37A3CEAC38029C638B099F01F044B09F30AD237D1786A1C", contentHash: "/wzJoTLh3ebeAzOdaT0yUXXznF4C/26eWS6js5dDzzgDKsxNpeOL+s0ZJTwaxZYj6wG5cr9I4rUYOzpXOWoW+w==", publicRestoreValidated: false });
  assert.deepEqual(evidence.checks, [{ name: "release-contract suite", command: "node --test tests/release-contract/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 328, passed: 328, failed: 0 }, sourceSha: sha }, { name: "Task 8 completion gate", command: "dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1", exitCode: null, status: "notRun", counts: null, passed: false, reason: "Not run at this evidence revision; Task 8 must refresh this manifest after its fixes and full gate.", sourceSha: sha }]);
  assert.deepEqual(evidence.artifacts, [{ kind: "OCI and package candidates", status: "absent/local-unpublished", digest: null }]);
  assert.deepEqual(Object.keys(evidence.commandInputs).sort(), commandInputs.sort());
  for (const input of commandInputs) assert.deepEqual(evidence.commandInputs[input], { value: null, reason: "Unperformed external gate; release operator must supply the immutable value.", owner: "release operator" });
  assert.deepEqual(Object.keys(evidence.externalGates).sort(), [...gateKeys].sort(), "external gate set must be exact");
  const owners = { publicPackageRestore: "release operator", hostedAccessibility: "release operator", protectedApprovals: "approver", artifactAttestation: "release operator", registrySigning: "release operator", immutableOciPromotion: "release operator", hostedSmokeSoak: "release operator", finalRelease: "approver" };
  for (const name of gateKeys) { const gate = evidence.externalGates[name]; assert.equal(gate.passed, false, `${name} must remain false`); assert.ok(gate.reason?.trim()); assert.equal(gate.owner, owners[name]); assert.equal(digest(gate.nextCommand), commandHashes[name]); assert.equal(gate.evidenceLink, null); assert.match(gate.nextCommand, /^pwsh -NoProfile -NonInteractive -File scripts\/release\/verify-task-12-external-gate\.ps1 -Gate [a-z-]+$/); }
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
    (copy) => { delete copy.localFeedPackage.contentHash; },
    (copy) => { copy.localFeedPackage.contentHash = "bad"; },
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
