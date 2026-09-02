import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const evidencePath = path.join(root, "docs/evidence/task-12-local-verification.json");
const sha = "fb983502c619bca7debb76eb7c01f436a9a6c913";
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
const pendingCommandHashes = { definitivePackageOciTuple: "b7698f795aa9b4728638f9ec29a28674bb8ed84523f8e4b88499654fb657520b" };
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
    capture(readinessText, /\*\*Accepted implementation revision:\*\* `([0-9a-f]{40})`/, "readiness accepted implementation"),
    capture(readinessText, /Task 12 local evidence manifest[^\n]*accepted implementation SHA `([0-9a-f]{40})`/, "readiness evidence binding"),
    capture(readinessText, /Task 12 repository implementation[^\n]*present through `([0-9a-f]{40})`/, "readiness remediation update"),
    capture(documentText, /Accepted Task 12 repository implementation source: `([0-9a-f]{40})`/, "handoff accepted implementation"),
    capture(documentText, /Expected:[^\n]*accepted Task 12 implementation source `([0-9a-f]{40})`/, "handoff expected history"),
    capture(documentText, /Task 12 local evidence ledger[^\n]*accepted implementation `([0-9a-f]{40})`/, "handoff evidence binding"),
    capture(documentText, /Task 12 repository implementation \| through `([0-9a-f]{7,40})`/, "handoff completion table"),
  ];
}
function validate(evidence, documentText = docs, planText = outerPlan, readinessText = readiness) {
  assert.equal(evidence.schema, "cmsify.task12-evidence.v1");
  assert.deepEqual(evidence.certification, {
    status: "preliminary-local-non-certifying",
    certifiesRelease: false,
    reason: "Local source, policy, public-package restore, and preserved-candidate evidence only; hosted candidate, approval, signing, promotion, soak, tag, and final-release gates remain unperformed.",
  });
  for (const key of ["sourceSha", "sdkVersion", "nodeVersion", "dockerClientVersion", "dockerServerVersion", "localFeedPackage", "checks", "artifacts", "externalGates", "pendingReleaseGates", "knownDiagnostics"]) assert.ok(key in evidence, `missing ${key}`);
  assert.equal(evidence.sourceSha, sha, "evidence must not transplant a stale source SHA");
  for (const acceptedSource of acceptedTask12Sources(documentText, readinessText)) assert.equal(acceptedSource, evidence.sourceSha, "current Task 12 documentation must agree with the evidence source SHA");
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
    { name: "release-contract suite", command: "node --test tests/release-contract/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 505, passed: 505, failed: 0 }, sourceSha: sha },
    { name: "release-smoke source suite", command: "node --test tests/release-smoke/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 91, passed: 91, failed: 0 }, sourceSha: sha },
    { name: "upgrade unit suite", command: "node --test tests/upgrade/unit/*.test.mjs", exitCode: 0, status: "passed", counts: { total: 173, passed: 173, failed: 0 }, sourceSha: sha },
    { name: "standalone release verifier", command: "node scripts/release/verify-release-contract.mjs", exitCode: 0, status: "passed", counts: null, sourceSha: sha },
    { name: "pre-publication full product sweep", command: "dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal", exitCode: 0, status: "passed", counts: { total: 599, passed: 599, failed: 0 }, sourceSha: "4d9da511303e646c5f4147f51108bf3d87c4bba0", reason: "Accepted head only adds release-policy, smoke-evidence, workflow, and test changes after this successful ignored-feed product sweep; it is retained as preliminary local evidence, not final public-candidate certification." },
    { name: "pre-publication TypeScript SDK sweep", command: "npm run generate:check && npm run typecheck && npm test && npm run build", exitCode: 0, status: "passed", counts: { total: 40, passed: 40, failed: 0 }, sourceSha: "4d9da511303e646c5f4147f51108bf3d87c4bba0", reason: "Accepted head only adds release-policy, smoke-evidence, workflow, and test changes after this successful ignored-feed product sweep; it is retained as preliminary local evidence, not final public-candidate certification." },
  ]);
  assert.deepEqual(evidence.artifacts, [{
    kind: "preserved preliminary API OCI candidate",
    status: "local-offline-loader-certified-non-promotable",
    releaseCandidate: false,
    version: "1.0.0-task12.a8e2218",
    sourceSha: "a8e2218c530b4323e8e44ca0cf25b3d22e2aea4d",
    loaderSourceSha: "4d9da511303e646c5f4147f51108bf3d87c4bba0",
    archiveSha256: "535CCD85AE5CED158D396534231F0D32E4ADA2ADD63EB089A499B07547236488",
    metadataSha256: "81BE3D015CC3E67C86221A858BCEF8550DC8ABDC5C8F4969D8A9D609FEFD35F3",
    manifestDigest: "sha256:f5ca59c7bab1dcb24ecf9ffadbc1daf819cfedbc7419c87bd5e07d5b80b8d79a",
    imageId: "sha256:69c7c6bb684f308e46b9385a3984b16fc2b6aa1600e542f7546c1cbb0d84b60b",
    platform: "linux/amd64",
    offlineLoaderLiveCertified: true,
    cleanupVerified: true,
    reason: "Reviewed live loader proof for the preserved API archive only; it predates the accepted implementation SHA and is not the definitive public package/API/Admin candidate tuple.",
  }]);
  assert.deepEqual(Object.keys(evidence.commandInputs).sort(), commandInputs.sort());
  for (const input of commandInputs) assert.deepEqual(evidence.commandInputs[input], { value: null, reason: "Unperformed external gate; release operator must supply the immutable value.", owner: "release operator" });
  assert.deepEqual(Object.keys(evidence.externalGates).sort(), [...gateKeys].sort(), "external gate set must be exact");
  const owners = { publicPackageRestore: "release operator", hostedAccessibility: "release operator", protectedApprovals: "approver", artifactAttestation: "release operator", registrySigning: "release operator", immutableOciPromotion: "release operator", hostedSmokeSoak: "release operator", finalRelease: "approver" };
  for (const name of gateKeys) {
    const gate = evidence.externalGates[name];
    assert.equal(gate.passed, name === "publicPackageRestore", `${name} completion must match the performed gates`);
    assert.ok(gate.reason?.trim());
    assert.equal(gate.owner, owners[name]);
    assert.equal(digest(gate.nextCommand), commandHashes[name]);
    assert.equal(gate.evidenceLink, name === "publicPackageRestore" ? "https://github.com/Syntax-Circus/SyntaxCircus.Http.Resilience/actions/runs/33404390063" : null);
    assert.match(gate.nextCommand, /^pwsh -NoProfile -NonInteractive -File scripts\/release\/verify-task-12-external-gate\.ps1 -Gate [a-z-]+$/);
  }
  assert.deepEqual(Object.keys(evidence.pendingReleaseGates).sort(), [...pendingGateKeys].sort(), "pending release gate set must be exact");
  const pendingOwners = { definitivePackageOciTuple: "authorized maintainer", finalConsumersAccessibilityUpgradeSmoke: "release operator", soak: "release operator", stableTag: "approver and release operator" };
  for (const name of pendingGateKeys) { const gate = evidence.pendingReleaseGates[name]; assert.equal(gate.passed, false, `${name} must remain false`); assert.equal(gate.status, "unperformed"); assert.ok(gate.reason?.trim()); assert.equal(gate.owner, pendingOwners[name]); assert.ok(gate.nextCommand?.trim()); assert.equal(gate.evidenceLink, null); }
  assert.equal(digest(evidence.pendingReleaseGates.definitivePackageOciTuple.nextCommand), pendingCommandHashes.definitivePackageOciTuple);
  assert.doesNotMatch(evidence.pendingReleaseGates.definitivePackageOciTuple.nextCommand, /gh workflow run/i);
  assert.match(evidence.pendingReleaseGates.definitivePackageOciTuple.reason, /explicit authorization/i);
  assert.match(evidence.pendingReleaseGates.definitivePackageOciTuple.reason, /v-prefixed SemVer tag/i);
  assert.match(releaseRunbook, /authorized maintainer pushes a validated SemVer tag to trigger the tracked `publish-cmsify\.yml` workflow/i);
  assert.match(publishWorkflow, /^on:\s*\n\s+push:\s*\n\s+tags: \["v\*"\]/m);
  assert.doesNotMatch(publishWorkflow, /^\s+workflow_dispatch:/m);
  assert.ok(evidence.knownDiagnostics.some((item) => /exact SyntaxCircus\.Http\.Resilience 0\.2\.0-cmsify\.1/i.test(item) && /public restore/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /CODEOWNERS/i.test(item) && /verified/i.test(item)));
  assert.ok(evidence.knownDiagnostics.some((item) => /historical media/i.test(item) && /599\/599/i.test(item)));
  assert.match(documentText, /task-12-local-verification\.json/);
  assert.match(documentText, /not ready/i);
  assert.doesNotMatch(documentText, /70afef5c647f53944fd61d1b35f40ece940aacf7/);
  assert.match(documentText, /preliminary local source\/policy tuple/i);
  assert.match(documentText, /offline-loader live certification/i);
  assert.match(readinessText, /^### Historical readiness ratings \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Historical evidence collected \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Historical API and SDK surface matrix \(superseded 2026-08-30\)$/m);
  assert.match(readinessText, /^## Current SyntaxCircus package disposition$/m);
  assert.match(readinessText, /^## Completed repository remediation and current release remainder$/m);
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
  for (const claim of [/hosted (?:validation|checks|workflow) (?:passed|succeeded|validated|green|certified)/i, /v1 certified/i, /release[- ]ready/i]) assert.ok(!documentText.split(/\r?\n/).some((line) => claim.test(line)));
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
    (copy) => { copy.artifacts[0].status = "local-published"; }, (copy) => { copy.artifacts[0].status = "promotion-complete"; }, (copy) => { copy.artifacts[0].releaseCandidate = true; }, (copy) => { copy.artifacts[0].manifestDigest = copy.artifacts[0].imageId; },
    (copy) => { copy.sourceSha = "0".repeat(40); },
    (copy) => { copy.certification.certifiesRelease = true; },
    (copy) => { copy.certification.status = "certified"; },
    (copy) => { copy.externalGates.publicPackageRestore.passed = false; },
    (copy) => { delete copy.externalGates.hostedAccessibility; },
    (copy) => { delete copy.externalGates.registrySigning.reason; },
    (copy) => { delete copy.commandInputs.CMSIFY_API_DIGEST; },
    (copy) => { delete copy.pendingReleaseGates.stableTag; },
    ...pendingGateKeys.flatMap((key) => [(copy) => { copy.pendingReleaseGates[key].passed = true; }, (copy) => { copy.pendingReleaseGates[key].status = "passed"; }, (copy) => { copy.pendingReleaseGates[key].nextCommand = ""; }, (copy) => { copy.pendingReleaseGates[key].evidenceLink = "x"; }]),
    ...gateKeys.flatMap((key) => [(copy) => { copy.externalGates[key].owner = "repository administrator"; }, (copy) => { copy.externalGates[key].nextCommand = "echo changed"; }, (copy) => { delete copy.externalGates[key].owner; }, (copy) => { copy.externalGates[key].reason = ""; }, (copy) => { copy.externalGates[key].nextCommand = ""; }, (copy) => { copy.externalGates[key].evidenceLink = "x"; }, (copy) => { copy.externalGates[key].passed = !copy.externalGates[key].passed; }]),
    (copy) => { delete copy.externalGates.finalRelease.evidenceLink; },
    (copy) => { delete copy.checks[1].status; },
  ]) { const copy = structuredClone(evidence); const before = JSON.stringify(copy); mutate(copy); assert.notEqual(JSON.stringify(copy), before); assert.throws(() => validate(copy)); }
  for (const claim of ["Hosted validation succeeded although not final.", "v1 certified.", "Release-ready."]) { const changed = `${docs}\n${claim}`; assert.notEqual(changed, docs); assert.throws(() => validate(evidence, changed)); }
  for (const claim of ["Several required gates do not exist yet.", "**Enhance, then adopt**", "## Phased remediation backlog", "Complete Admin OIDC with shared authentication/token-forwarding packages."]) {
    const changed = `${readiness}\n${claim}`;
    assert.notEqual(changed, readiness);
    assert.throws(() => validate(evidence, docs, outerPlan, changed));
  }
  const section = task12Section(outerPlan); for (const checkbox of [...section.matchAll(/- \[ \]/g)]) { const position = checkbox.index; const changedSection = `${section.slice(0, position)}- [x]${section.slice(position + 5)}`; const changedPlan = outerPlan.replace(section, changedSection); assert.notEqual(changedPlan, outerPlan); assert.throws(() => validate(evidence, docs, changedPlan)); }
});
