import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const evidencePath = path.join(root, "docs/evidence/task-12-local-verification.json");
const sha = "70afef5c647f53944fd61d1b35f40ece940aacf7";
const docs = ["docs/v1-release-readiness.md", "docs/v1-release-remediation-handoff.md", "docs/superpowers/plans/2026-08-24-v1-remediation.md"].map((file) => readFileSync(path.join(root, file), "utf8")).join("\n");
const gateKeys = ["publicPackageRestore", "hostedAccessibility", "protectedApprovals", "artifactAttestation", "registrySigning", "immutableOciPromotion", "hostedSmokeSoak", "finalRelease"];

function load() { assert.ok(existsSync(evidencePath), "Task 12 evidence manifest must exist"); return JSON.parse(readFileSync(evidencePath, "utf8")); }
function validate(evidence, documentText = docs) {
  assert.equal(evidence.schema, "cmsify.task12-evidence.v1");
  for (const key of ["sourceSha", "sdkVersion", "nodeVersion", "dockerClientVersion", "dockerServerVersion", "localFeedPackage", "checks", "artifacts", "externalGates", "knownDiagnostics"]) assert.ok(key in evidence, `missing ${key}`);
  assert.equal(evidence.sourceSha, sha, "evidence must not transplant a stale source SHA");
  assert.deepEqual(evidence.localFeedPackage, { id: "SyntaxCircus.Http.Resilience", version: "0.2.0-cmsify.1", sha256: "17843D8C0A3422FCE37A3CEAC38029C638B099F01F044B09F30AD237D1786A1C", publicRestoreValidated: false });
  assert.ok(evidence.checks.every((check) => typeof check.name === "string" && typeof check.command === "string" && check.command && check.sourceSha === sha && check.status === "passed" ? check.exitCode === 0 && check.counts : check.status === "notRun" && check.exitCode === null && check.counts === null && check.passed === false && typeof check.reason === "string"), "checks need explicit passed/notRun structure, command, counts, and source SHA");
  assert.ok(evidence.artifacts.every((artifact) => typeof artifact.kind === "string" && typeof artifact.status === "string" && !["published", "promoted"].includes(artifact.status.toLowerCase())), "artifacts must be structured and local only");
  assert.deepEqual(Object.keys(evidence.externalGates).sort(), [...gateKeys].sort(), "external gate set must be exact");
  for (const name of gateKeys) { const gate = evidence.externalGates[name]; assert.equal(gate.passed, false, `${name} must remain false`); assert.equal(typeof gate.reason, "string"); assert.ok("evidenceLink" in gate); }
  assert.ok(evidence.knownDiagnostics.some((item) => /unrecognized contract step sequence/i.test(item) && /Task 8/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /concessive hosted-claim exemption/i.test(item) && /Task 8/i.test(item)));
  assert.match(documentText, /task-12-local-verification\.json/);
  assert.match(documentText, /not ready/i);
  for (const claim of [/public(?:\/CI)? restore (?:passed|succeeded|validated|green|certified)/i, /hosted (?:validation|checks|workflow) (?:passed|succeeded|validated|green|certified)/i, /repository implementation complete/i, /v1 certified/i, /release[- ]ready/i]) assert.ok(!documentText.split(/\r?\n/).some((line) => claim.test(line) && !/\b(?:not|does not|do not|never)\b/i.test(line)));
  assert.doesNotMatch(documentText, /(?:although|despite|while).*?(?:public restore|hosted).*?(?:passed|succeeded|validated|green|certified)/i);
  assert.doesNotMatch(documentText, /^\s*-\s*\[x\].*(?:final release|public restore|hosted)/im);
}
test("Task 12 evidence is complete, SHA-bound, and honest", () => validate(load()));
test("Task 12 evidence mutations are rejected", () => {
  const evidence = load();
  for (const mutate of [
    (copy) => { delete copy.checks[0].command; },
    (copy) => { delete copy.checks[0].counts; },
    (copy) => { copy.localFeedPackage.sha256 = "bad"; },
    (copy) => { copy.localFeedPackage.publicRestoreValidated = true; },
    (copy) => { copy.artifacts[0].status = "published"; },
    (copy) => { copy.sourceSha = "0".repeat(40); },
    (copy) => { copy.externalGates.publicPackageRestore.passed = true; },
    (copy) => { delete copy.externalGates.hostedAccessibility; },
    (copy) => { delete copy.externalGates.registrySigning.reason; },
    (copy) => { delete copy.externalGates.finalRelease.evidenceLink; },
    (copy) => { delete copy.checks[1].status; },
  ]) { const copy = structuredClone(evidence); const before = JSON.stringify(copy); mutate(copy); assert.notEqual(JSON.stringify(copy), before); assert.throws(() => validate(copy)); }
  for (const claim of ["Public restore passed.", "Hosted validation succeeded.", "Repository implementation complete.", "v1 certified.", "Release-ready.", "Although public restore passed, it is not ready.", "- [x] Final release"]) { const changed = `${docs}\n${claim}`; assert.notEqual(changed, docs); assert.throws(() => validate(evidence, changed)); }
});
