import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const evidencePath = path.join(root, "docs/evidence/task-12-local-verification.json");
const sha = "8f4944de68aac5690f369b62e74e8464df1164b2";
const releaseSourceSha = "26c064a81411c1ec303fa1dc07813841760d44ea";
const recorderSourceSha = "68d8e4cffc4873c5595ad37efc5849ecc7393f61";
const readiness = readFileSync(path.join(root, "docs/v1-release-readiness.md"), "utf8");
const handoff = readFileSync(path.join(root, "docs/v1-release-remediation-handoff.md"), "utf8");
const releaseRunbook = readFileSync(path.join(root, "docs/release-runbook.md"), "utf8");
const publishWorkflow = readFileSync(path.join(root, ".github/workflows/publish-cmsify.yml"), "utf8");
const docs = ["docs/v1-release-readiness.md", "docs/v1-release-remediation-handoff.md", "docs/superpowers/plans/2026-08-24-v1-remediation.md"].map((file) => readFileSync(path.join(root, file), "utf8")).join("\n");
const outerPlan = readFileSync(path.join(root, "docs/superpowers/plans/2026-08-24-v1-remediation.md"), "utf8");
const gateKeys = ["publicPackageRestore", "hostedAccessibility", "protectedApprovals", "artifactAttestation", "registrySigning", "immutableOciPromotion", "hostedSmokeSoak", "finalRelease"];
const pendingGateKeys = ["definitivePackageOciTuple", "finalConsumersAccessibilityUpgradeSmoke", "soak", "stableTag"];
const commandInputs = ["CMSIFY_RELEASE_RUN_ID", "CMSIFY_CHECKSUMS_PATH", "CMSIFY_API_DIGEST", "CMSIFY_ADMIN_DIGEST", "CMSIFY_RELEASE_VERSION", "CMSIFY_RELEASE_TAG", "CMSIFY_COSIGN_CERTIFICATE_IDENTITY", "CMSIFY_ATTESTATION_SIGNER_WORKFLOW", "CMSIFY_ACCESSIBILITY_JOB_ID", "CMSIFY_PROMOTE_JOB_ID", "CMSIFY_SMOKE_JOB_ID", "CMSIFY_UPGRADE_ROLLBACK_JOB_ID", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_SOAK_EVIDENCE_PATH", "CMSIFY_SOAK_EVIDENCE_SHA256", "CMSIFY_SOAK_RECORDER_RUN_ID", "CMSIFY_SOAK_RECORDER_SOURCE_SHA", "CMSIFY_SOAK_ATTESTATION_SIGNER_WORKFLOW"];
const commandHashes = { publicPackageRestore: "8c5f197769a0dfb1827c8c8e591241a84b4baf5e1b86cd5ba69ea557dba90802", hostedAccessibility: "46595d036b67d564535df78fe855d955a6334ae09011143ac9cac402cb71b87a", protectedApprovals: "82015b66e81fb44874922df638a226dca0aefb3f24cc6ed758fdd4ee5c7bf6b1", artifactAttestation: "97edc3457d53334c86ba3d7093d7db95d613067a1c0448cf699a7611158f36bd", registrySigning: "f07c8b1ff5e827bcf356bb261e48e29f35922f02e15fceb174d44d8a2ca628c3", immutableOciPromotion: "791ba0b7d42776b4f02520cd0bfdcd67560a2205379e5092443ee399309f7f22", hostedSmokeSoak: "99670c79701613787772dc0b7b5626886ecaf4d14c1e87c60fd2b7d121d85022", finalRelease: "fb57f80e7a61ee08f4ca5eccff12c42fd16b846a9a69884fc8d8d8db2c2f529f" };
const staleReadinessClaims = [
  /several required gates do not exist yet/i,
  /runtime-image digest pins, repository-wide action-pin audit, SBOM\/signing, accessibility-trigger expansion, production-like artifact smoke, governance, and final release certification remain open/i,
  /TypeScript generated schema types are exported, but the generated `createCmsifyFetchClient` factory is not exported/i,
  /`Http\.Resilience` 0\.1\.6 is pinned but unused/i,
  /\*\*Enhance, then adopt\*\*/i,
  /^## Phased remediation backlog$/m,
  /Consolidate API boundary contracts and select one pagination\/error convention/i,
  /Complete Admin OIDC with shared authentication\/token-forwarding packages/i,
  /Release and consume the required `SyntaxCircus\.AspNetCore\.Authentication`/i,
];
const digest = (value) => createHash("sha256").update(value).digest("hex");

function load() { assert.ok(existsSync(evidencePath), "Task 12 evidence manifest must exist"); return JSON.parse(readFileSync(evidencePath, "utf8")); }
function task12Section(plan) { return plan.match(/### Task 12:[\s\S]*?(?=\n## Completion Gate)/)?.[0] ?? ""; }
function acceptedTask12Sources(documentText, readinessText) {
  const capture = (text, pattern, context) => {
    const match = text.match(pattern);
    assert.ok(match, `missing ${context} source reference`);
    return match[1];
  };
  return [
    capture(readinessText, /\*\*Certification verifier revision:\*\* `([0-9a-f]{40})`/, "readiness verifier revision"),
    capture(readinessText, /Task 12 certification manifest[^\n]*verifier revision `([0-9a-f]{40})`/, "readiness evidence binding"),
    capture(documentText, /Certification verifier revision: `([0-9a-f]{40})`/, "handoff verifier revision"),
  ];
}
function validate(evidence, documentText = docs, planText = outerPlan, readinessText = readiness) {
  assert.equal(evidence.schema, "cmsify.task12-evidence.v1");
  assert.deepEqual(evidence.certification, {
    status: "certified",
    certifiesRelease: true,
    reason: "Cmsify 0.2.1 was built once from the exact v0.2.1 source, passed every local and hosted certification gate, received the required protected-environment approval, was promoted without rebuilding, and completed an authenticated soak longer than 60 minutes.",
  });
  for (const key of ["sourceSha", "release", "sdkVersion", "nodeVersion", "dockerClientVersion", "dockerServerVersion", "localFeedPackage", "checks", "artifacts", "externalGates", "pendingReleaseGates", "knownDiagnostics"]) assert.ok(key in evidence, `missing ${key}`);
  assert.equal(evidence.sourceSha, sha, "evidence must not transplant a stale source SHA");
  for (const acceptedSource of acceptedTask12Sources(documentText, readinessText)) assert.equal(acceptedSource, evidence.sourceSha, "current Task 12 documentation must agree with the evidence source SHA");
  assert.deepEqual(evidence.release, {
    version: "0.2.1",
    tag: "v0.2.1",
    sourceSha: releaseSourceSha,
    releaseRunId: "33630027328",
    publishedAtUtc: "2026-09-02T13:10:34Z",
    url: "https://github.com/Syntax-Circus/cmsify/releases/tag/v0.2.1",
  });
  assert.equal(evidence.sdkVersion, "10.0.400"); assert.equal(evidence.nodeVersion, "v24.14.1"); assert.equal(evidence.dockerClientVersion, "29.7.2"); assert.equal(evidence.dockerServerVersion, null);
  assert.deepEqual(evidence.localFeedPackage, {
    id: "SyntaxCircus.Http.Resilience",
    version: "0.2.0-cmsify.1",
    defaultBranchSourceSha: "827aafb7f9eaa8e35c67c3a73aa5bd761384899a",
    localUnsignedSha256: "44912C98E653C2414D42BDD5174478DF184D02AFDA6E95C1DD1996F4C81C40B8",
    publicSignedSha256: "3C2D87EF5B1C5D3FD49A4EB57B89EF3A251841B03FDC1F762256ED26B8BE0E65",
    contentHash: "NMTysZp25vAOrFJ67PkFUbWleijS54StWjT5GAXbRLWoxv+H69VBFgP6f+wR0YRTNvF/yMvlIallZu3ID7ue6w==",
    expectedRepositorySignature: { type: "Repository", serviceIndex: "https://api.nuget.org/v3/index.json", owner: "syntaxcircus" },
    publicRestoreValidated: true,
  });
  assert.deepEqual(evidence.checks, [
    { name: "release-contract suite", command: "node --test tests/release-contract/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 516, passed: 516, failed: 0 }, sourceSha: sha },
    { name: "release-smoke source suite", command: "node --test tests/release-smoke/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 91, passed: 91, failed: 0 }, sourceSha: recorderSourceSha },
    { name: "upgrade unit suite", command: "node --test tests/upgrade/unit/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 173, passed: 173, failed: 0 }, sourceSha: recorderSourceSha },
    { name: "standalone release verifier", command: "node scripts/release/verify-release-contract.mjs", exitCode: 0, status: "passed", counts: null, sourceSha: sha },
    { name: "pre-publication full product sweep", command: "dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal", exitCode: 0, status: "passed", counts: { total: 599, passed: 599, failed: 0 }, sourceSha: "4d9da511303e646c5f4147f51108bf3d87c4bba0", reason: "The later exact tag workflow rebuilt from public dependencies and passed its complete build, candidate, accessibility, smoke, upgrade/rollback, and promotion graph." },
    { name: "pre-publication TypeScript SDK sweep", command: "npm run generate:check && npm run typecheck && npm test && npm run build", exitCode: 0, status: "passed", counts: { total: 40, passed: 40, failed: 0 }, sourceSha: "4d9da511303e646c5f4147f51108bf3d87c4bba0", reason: "The packed npm artifact was subsequently certified and published by the exact tag workflow." },
  ]);
  assert.deepEqual(evidence.artifacts, [{
    kind: "definitive same-source release candidate",
    status: "published-certified",
    releaseCandidate: true,
    version: "0.2.1",
    sourceSha: releaseSourceSha,
    releaseRunId: "33630027328",
    subjectCount: 13,
    checksumsSha256: "c6e08df96d04d6d3af4c973e9b2ba416cbc397e22ffba0d04305769f76b3f0eb",
    releaseManifestSha256: "293e0fc63e6e5916075e265fef1346f19890ce4bf76e1fa6e93b1d6a415d71dd",
    apiManifestDigest: "sha256:be1b34e4c61b305c9c7e1112bc52c2e25898a2c8ca300847c7909639f7aca6b7",
    adminManifestDigest: "sha256:bfd965b6d94fe95543086c3665b5dfc13f08204f13a4855719775c0200dd6306",
    attestationSignerWorkflow: "Syntax-Circus/cmsify/.github/workflows/publish-cmsify.yml",
    reason: "All thirteen canonical files passed checksum, manifest, and exact-source attestation verification; packages and OCI manifests were published or promoted without rebuilding.",
  }]);
  assert.deepEqual(Object.keys(evidence.commandInputs).sort(), commandInputs.sort());
  for (const input of commandInputs) {
    assert.equal(typeof evidence.commandInputs[input].value, "string", `${input} must record its performed value`);
    assert.ok(evidence.commandInputs[input].value.trim(), `${input} must not be blank`);
    assert.ok(evidence.commandInputs[input].reason?.trim(), `${input} must explain its provenance`);
    assert.equal(evidence.commandInputs[input].owner, "release operator");
  }
  assert.equal(evidence.commandInputs.CMSIFY_RELEASE_SOURCE_SHA.value, releaseSourceSha);
  assert.equal(evidence.commandInputs.CMSIFY_SOAK_RECORDER_SOURCE_SHA.value, recorderSourceSha);
  assert.equal(evidence.commandInputs.CMSIFY_SOAK_EVIDENCE_SHA256.value, "c2fba7d5756d48d5d9e6ac2e761edc6b8a657f2996afeddbd1134125a7a17a22");
  assert.deepEqual(Object.keys(evidence.externalGates).sort(), [...gateKeys].sort(), "external gate set must be exact");
  const owners = { publicPackageRestore: "release operator", hostedAccessibility: "release operator", protectedApprovals: "approver", artifactAttestation: "release operator", registrySigning: "release operator", immutableOciPromotion: "release operator", hostedSmokeSoak: "release operator", finalRelease: "approver" };
  for (const name of gateKeys) {
    const gate = evidence.externalGates[name];
    assert.equal(gate.passed, true, `${name} must be certified`);
    assert.ok(gate.reason?.trim());
    assert.equal(gate.owner, owners[name]);
    assert.equal(digest(gate.nextCommand), commandHashes[name]);
    assert.match(gate.evidenceLink, /^https:\/\/github\.com\/Syntax-Circus\//);
    assert.match(gate.nextCommand, /^pwsh -NoProfile -NonInteractive -File scripts\/release\/verify-task-12-external-gate\.ps1 -Gate [a-z-]+$/);
  }
  assert.deepEqual(Object.keys(evidence.pendingReleaseGates).sort(), [...pendingGateKeys].sort(), "pending release gate set must be exact");
  const pendingOwners = { definitivePackageOciTuple: "authorized maintainer", finalConsumersAccessibilityUpgradeSmoke: "release operator", soak: "release operator", stableTag: "approver and release operator" };
  for (const name of pendingGateKeys) { const gate = evidence.pendingReleaseGates[name]; assert.equal(gate.passed, true, `${name} must be complete`); assert.equal(gate.status, "passed"); assert.ok(gate.reason?.trim()); assert.equal(gate.owner, pendingOwners[name]); assert.match(gate.nextCommand, /^No action required;/); assert.match(gate.evidenceLink, /^https:\/\/github\.com\/Syntax-Circus\/cmsify\//); }
  assert.match(releaseRunbook, /authorized maintainer pushes a validated SemVer tag to trigger the tracked `publish-cmsify\.yml` workflow/i);
  assert.match(publishWorkflow, /^on:\s*\n\s+push:\s*\n\s+tags: \["v\*"\]/m);
  assert.doesNotMatch(publishWorkflow, /^\s+workflow_dispatch:/m);
  assert.ok(evidence.knownDiagnostics.some((item) => /exact SyntaxCircus\.Http\.Resilience 0\.2\.0-cmsify\.1/i.test(item) && /public restore/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /CODEOWNERS/i.test(item) && /non-blocking/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /historical media/i.test(item) && /closed as release blockers/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /rollback diagnostic omission is closed/i.test(item)));
  assert.match(documentText, /task-12-local-verification\.json/);
  assert.match(documentText, /v0\.2\.1[^\n]*(?:certified|released)|(?:certified|released)[^\n]*v0\.2\.1/i);
  assert.doesNotMatch(documentText, /Current release is not certified|Hosted validation remains unperformed|The release is not ready/i);
  assert.doesNotMatch(documentText, /70afef5c647f53944fd61d1b35f40ece940aacf7/);
  assert.match(readinessText, /^### Historical readiness ratings \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Historical evidence collected \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Historical API and SDK surface matrix \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Current SyntaxCircus package disposition$/m);
  assert.match(readinessText, /^## Completed repository remediation and release certification$/m);
  for (const claim of staleReadinessClaims) assert.doesNotMatch(readinessText, claim);
  for (const publicationDocument of [readinessText, handoff]) {
    assert.match(publicationDocument, /(?:0\.2\.0-cmsify\.1[\s\S]{0,300}published|published[\s\S]{0,300}0\.2\.0-cmsify\.1)/i);
    assert.match(publicationDocument, /replacement[^.\n]*(?:separate|explicit)[^.\n]*approv[^.\n]*(?:identity|pin)[^.\n]*review|(?:separate|explicit)[^.\n]*approv[^.\n]*replacement[^.\n]*(?:identity|pin)[^.\n]*review/i);
    assert.doesNotMatch(publicationDocument, /must[^.\n]*(?:publish|provide)[^.\n]*(?:exact )?stable `?SyntaxCircus\.Http\.Resilience|must[^.\n]*replace[^.\n]*0\.2\.0-cmsify\.1[^.\n]*stable/i);
  }
  for (let index = 1; index <= 19; index += 1) {
    const finding = `F-${String(index).padStart(2, "0")}`;
    const section = index <= 13
      ? readinessText.match(new RegExp(`#### ${finding}[^\\n]*[\\s\\S]*?(?=\\n#### F-|\\n### Medium findings)`))?.[0] ?? ""
      : readinessText.split(/\r?\n/).find((line) => line.startsWith(`| ${finding} |`)) ?? "";
    assert.match(section, /remediated (?:locally|at the (?:local )?source(?:\/policy)? level)/i, `${finding} must state its current source remediation status`);
  }
  assert.match(documentText, /passed all required local and hosted gates/i);
  assert.match(documentText, /release[- ]ready/i);
  const section = task12Section(planText); assert.ok(section); assert.ok(/^\s*- \[x\]/im.test(section)); assert.doesNotMatch(section, /^\s*- \[ \]/m);
}
test("Task 12 evidence is complete, SHA-bound, and honest", () => validate(load()));
test("Task 12 evidence mutations are rejected", () => {
  const evidence = load();
  for (const mutate of [
    (copy) => { delete copy.checks[0].command; },
    (copy) => { delete copy.checks[0].counts; },
    (copy) => { copy.checks = []; }, (copy) => { copy.artifacts = []; },
    (copy) => { copy.sdkVersion = "0"; }, (copy) => { copy.nodeVersion = "v0"; }, (copy) => { copy.dockerClientVersion = "0"; }, (copy) => { copy.dockerServerVersion = "0"; },
    (copy) => { copy.checks[0].command = "stale"; }, (copy) => { copy.checks[0].counts.total = 0; }, (copy) => { copy.checks[4].reason = " "; },
    (copy) => { copy.localFeedPackage.localUnsignedSha256 = "bad"; },
    (copy) => { copy.localFeedPackage.defaultBranchSourceSha = "bad"; },
    (copy) => { copy.localFeedPackage.publicSignedSha256 = "bad"; },
    (copy) => { delete copy.localFeedPackage.contentHash; },
    (copy) => { copy.localFeedPackage.contentHash = "bad"; },
    (copy) => { copy.localFeedPackage.expectedRepositorySignature.type = "Author"; },
    (copy) => { copy.localFeedPackage.expectedRepositorySignature.serviceIndex = "https://example.invalid/v3/index.json"; },
    (copy) => { copy.localFeedPackage.expectedRepositorySignature.owner = "attacker"; },
    (copy) => { copy.localFeedPackage.publicRestoreValidated = false; },
    (copy) => { copy.artifacts[0].status = "unpublished"; }, (copy) => { copy.artifacts[0].releaseCandidate = false; }, (copy) => { copy.artifacts[0].apiManifestDigest = copy.artifacts[0].adminManifestDigest; },
    (copy) => { copy.sourceSha = "0".repeat(40); },
    (copy) => { copy.certification.certifiesRelease = false; },
    (copy) => { copy.certification.status = "preliminary-local-non-certifying"; },
    (copy) => { copy.externalGates.publicPackageRestore.passed = false; },
    (copy) => { delete copy.externalGates.hostedAccessibility; },
    (copy) => { delete copy.externalGates.registrySigning.reason; },
    (copy) => { delete copy.commandInputs.CMSIFY_API_DIGEST; },
    (copy) => { delete copy.pendingReleaseGates.stableTag; },
    ...pendingGateKeys.flatMap((key) => [(copy) => { copy.pendingReleaseGates[key].passed = false; }, (copy) => { copy.pendingReleaseGates[key].status = "unperformed"; }, (copy) => { copy.pendingReleaseGates[key].nextCommand = ""; }, (copy) => { copy.pendingReleaseGates[key].evidenceLink = null; }]),
    ...gateKeys.flatMap((key) => [(copy) => { copy.externalGates[key].owner = "repository administrator"; }, (copy) => { copy.externalGates[key].nextCommand = "echo changed"; }, (copy) => { delete copy.externalGates[key].owner; }, (copy) => { copy.externalGates[key].reason = ""; }, (copy) => { copy.externalGates[key].nextCommand = ""; }, (copy) => { copy.externalGates[key].evidenceLink = "x"; }, (copy) => { copy.externalGates[key].passed = !copy.externalGates[key].passed; }]),
    (copy) => { delete copy.externalGates.finalRelease.evidenceLink; },
    (copy) => { delete copy.checks[1].status; },
  ]) { const copy = structuredClone(evidence); const before = JSON.stringify(copy); mutate(copy); assert.notEqual(JSON.stringify(copy), before); assert.throws(() => validate(copy)); }
  for (const claim of ["Current release is not certified.", "Hosted validation remains unperformed.", "The release is not ready."]) { const changed = `${docs}\n${claim}`; assert.notEqual(changed, docs); assert.throws(() => validate(evidence, changed)); }
  for (const claim of ["Several required gates do not exist yet.", "**Enhance, then adopt**", "## Phased remediation backlog", "Complete Admin OIDC with shared authentication/token-forwarding packages."]) {
    const changed = `${readiness}\n${claim}`;
    assert.notEqual(changed, readiness);
    assert.throws(() => validate(evidence, docs, outerPlan, changed));
  }
  const section = task12Section(outerPlan); for (const checkbox of [...section.matchAll(/- \[x\]/gi)]) { const position = checkbox.index; const changedSection = `${section.slice(0, position)}- [ ]${section.slice(position + 5)}`; const changedPlan = outerPlan.replace(section, changedSection); assert.notEqual(changedPlan, outerPlan); assert.throws(() => validate(evidence, docs, changedPlan)); }
});
