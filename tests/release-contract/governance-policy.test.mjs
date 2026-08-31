import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { governanceDocumentClauses, honestHostedPrerequisiteDisclaimers, validateGovernanceContract } from "../../scripts/release/governance-validator.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

function document(path) {
  const fullPath = resolve(repositoryRoot, path);
  assert.equal(existsSync(fullPath), true, `${path} must exist`);
  return readFileSync(fullPath, "utf8");
}

function governanceDocuments() {
  return Object.fromEntries(["docs/api-compatibility.md", "SECURITY.md", "SUPPORT.md", ".github/CODEOWNERS", "docs/release-runbook.md", "docs/rollback-runbook.md"].map((path) => [path, document(path)]));
}

function insertContractStepAtBoundary(source, boundary, stepLines) {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const contractIndex = lines.indexOf("  contract:");
  const approvalIndex = lines.indexOf("  breaking_change_approval:");
  assert.notEqual(contractIndex, -1, "contract job must exist");
  assert.notEqual(approvalIndex, -1, "approval job must exist");
  const stepIndexes = lines.flatMap((line, index) => index > contractIndex && index < approvalIndex && /^      - /.test(line) ? [index] : []);
  const boundaries = [...stepIndexes, approvalIndex];
  assert.equal(boundaries.length, 13, "the canonical contract job must expose every step boundary");
  lines.splice(boundaries[boundary], 0, ...stepLines);
  return lines.join("\n");
}

test("real governance validator ignores decoys outside the three OpenAPI jobs", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const decoy = `# HEAD_SHA="${"${{ github.event.pull_request.head.sha }}"}"\n${workflow}`
    .replace('if: needs.contract.outputs.breaking == \'true\'', "if: always()")
    .replace('if: always()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check', 'if: success()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check');
  const result = validateGovernanceContract({ workflow: decoy });
  assert.equal(result.ok, false);
  assert.ok(result.errors.some((error) => /canonical active approval job/.test(error)));
  assert.ok(result.errors.some((error) => /canonical active gate job/.test(error)));
});

test("real governance validator rejects each commented, duplicate, or disabled critical structure mutation", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const mutations = [
    ["commented PR-head binding", (source) => source.replace('HEAD_SHA="${{ github.event.pull_request.head.sha }}"', '# HEAD_SHA="${{ github.event.pull_request.head.sha }}"')],
    ["checkout assertion replaced with inline-comment decoy", (source) => source.replace('test "$(git rev-parse HEAD)" = "$HEAD_SHA"', "true # required")],
    ["contract gate condition replaced with inline-comment decoy", (source) => source.replace('if [[ "${{ needs.contract.result }}" != "success" ]]; then', "if false; then # required condition")],
    ["approval gate condition replaced with inline-comment decoy", (source) => source.replace('if [[ "${{ needs.contract.outputs.breaking }}" == "true" && "${{ needs.breaking_change_approval.result }}" != "success" ]]; then', "if false; then # required condition")],
    ["disabled decoy revisions step with operational ID reassigned", (source) => source.replace('      - name: Resolve exact comparison revisions\n        id: revisions', '      - name: Resolve exact comparison revisions # decoy\n        id: other\n      - name: decoy revisions\n        id: revisions\n        if: false')],
    ["wrong operational revisions name hidden by comment", (source) => source.replace("      - name: Resolve exact comparison revisions", "      - name: unrelated # Resolve exact comparison revisions")],
    ["inline-commented duplicate revisions ID", (source) => source.replace("        id: revisions", "        id: revisions\n      - name: duplicate\n        id: revisions # duplicate")],
    ["quoted duplicate revisions ID", (source) => source.replace("        id: revisions", "        id: revisions\n      - name: duplicate\n        id: 'revisions'")],
    ["inline-commented duplicate contract job key", (source) => `${source}\n  contract: # duplicate\n    runs-on: ubuntu-latest\n`],
    ["quoted duplicate contract job key", (source) => `${source}\n  'contract':\n    runs-on: ubuntu-latest\n`],
    ["revisions step disabled", (source) => source.replace("        id: revisions", "        id: revisions\n        if: false")],
    ["revisions step continue-on-error", (source) => source.replace("        id: revisions", "        id: revisions\n        continue-on-error: false")],
    ["diff step disabled", (source) => source.replace("        id: diff", "        id: diff\n        if: false")],
    ["diff step continue-on-error", (source) => source.replace("        id: diff", "        id: diff\n        continue-on-error: false")],
    ["checkout step disabled", (source) => source.replace("          fetch-depth: 0", "        if: false\n          fetch-depth: 0")],
    ["checkout step continue-on-error", (source) => source.replace("          fetch-depth: 0", "        continue-on-error: false\n          fetch-depth: 0")],
  ];
  for (const [name, mutate] of mutations) {
    const candidate = mutate(workflow);
    assert.notEqual(candidate, workflow, `${name} mutation must change source`);
    assert.equal(validateGovernanceContract({ workflow: candidate }).ok, false, `${name} unexpectedly passed`);
  }
});

test("real governance validator rejects unreachable and decoy critical workflow bodies", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const mutations = [
    ["checkout decoy environment", (source) => source.replace("        with:\n", "        env:\n          DECOY: required\n        with:\n")],
    ["revisions heredoc decoy", (source) => source.replace('          git cat-file -e "$HEAD_SHA^{commit}"\n          test "$(git rev-parse HEAD)" = "$HEAD_SHA"\n          git cat-file -e "$BASE_SHA:sdk/typescript/openapi.snapshot.json"\n          echo "base-sha=$BASE_SHA" >> "$GITHUB_OUTPUT"\n          echo "head-sha=$HEAD_SHA" >> "$GITHUB_OUTPUT"', '          true;# required\n          cat <<\'REQUIRED\'\n          git cat-file -e "$HEAD_SHA^{commit}"\n          test "$(git rev-parse HEAD)" = "$HEAD_SHA"\n          git cat-file -e "$BASE_SHA:sdk/typescript/openapi.snapshot.json"\n          echo "base-sha=$BASE_SHA" >> "$GITHUB_OUTPUT"\n          echo "head-sha=$HEAD_SHA" >> "$GITHUB_OUTPUT"\n          REQUIRED')],
    ["diff unreachable alternate", (source) => source.replace('          docker run --rm -v "$RUNNER_TEMP:/work:ro" tufin/oasdiff:v1.28.0@sha256:86830f988eaafcf589acb2794ee5ab78e3300ded071d6517bf085469300cbf36 breaking /work/openapi-base.json /work/openapi-head.json --match-path \'^/api/v1(?:/|$)\' --fail-on ERR > "$RUNNER_TEMP/oasdiff.txt" 2>&1\n          result=$?', '          if false; then\n            docker run --rm -v "$RUNNER_TEMP:/work:ro" tufin/oasdiff:v1.28.0@sha256:86830f988eaafcf589acb2794ee5ab78e3300ded071d6517bf085469300cbf36 breaking /work/openapi-base.json /work/openapi-head.json --match-path \'^/api/v1(?:/|$)\' --fail-on ERR > "$RUNNER_TEMP/oasdiff.txt" 2>&1\n            result=$?\n          fi')],
    ["gate unreachable conditions", (source) => source.replace('          if [[ "${{ needs.contract.result }}" != "success" ]]; then\n            echo "The OpenAPI contract job did not succeed." >&2\n            exit 1\n          fi\n          if [[ "${{ needs.contract.outputs.breaking }}" == "true" && "${{ needs.breaking_change_approval.result }}" != "success" ]]; then\n            echo "A breaking /api/v1 change requires successful protected approval evidence." >&2\n            exit 1\n          fi', '          if false; then\n            if [[ "${{ needs.contract.result }}" != "success" ]]; then\n              echo "The OpenAPI contract job did not succeed." >&2\n              exit 1\n            fi\n            if [[ "${{ needs.contract.outputs.breaking }}" == "true" && "${{ needs.breaking_change_approval.result }}" != "success" ]]; then\n              echo "A breaking /api/v1 change requires successful protected approval evidence." >&2\n              exit 1\n            fi\n          fi')],
  ];
  for (const [name, mutate] of mutations) {
    const candidate = mutate(workflow);
    assert.notEqual(candidate, workflow, `${name} mutation must change source`);
    assert.equal(validateGovernanceContract({ workflow: candidate }).ok, false, `${name} unreachable/decoy mutation unexpectedly passed`);
  }
});

test("real governance validator rejects extra run, uses, and env steps at every contract-job boundary", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const unexpectedSteps = [
    ["run", ["      - name: Unexpected run mutation", "        run: echo mutation"]],
    ["uses", ["      - uses: example.invalid/unexpected/action@0123456789abcdef0123456789abcdef01234567"]],
    ["env", ["      - name: Unexpected environment mutation", "        env:", "          MUTATION: enabled", "        run: echo mutation"]],
  ];
  for (let boundary = 0; boundary < 13; boundary += 1) for (const [kind, stepLines] of unexpectedSteps) {
    const candidate = insertContractStepAtBoundary(workflow, boundary, stepLines);
    assert.notEqual(candidate, workflow, `boundary ${boundary} ${kind} mutation must change source`);
    assert.equal(validateGovernanceContract({ workflow: candidate }).ok, false, `boundary ${boundary} extra ${kind} step unexpectedly passed`);
  }
});

function requireClauses(path, clauses) {
  const contents = document(path);
  for (const clause of clauses) assert.match(contents, clause, `${path} is missing required governance policy`);
  return contents;
}

const openApiInvariants = [
  ["exact PR head checkout", /ref: \$\{\{ github\.event_name == 'pull_request' && github\.event\.pull_request\.head\.sha \|\| github\.sha \}\}/],
  ["event-specific PR head identity", /HEAD_SHA="\$\{\{ github\.event\.pull_request\.head\.sha \}\}"/],
  ["checked-out head verification", /test "\$\(git rev-parse HEAD\)" = "\$HEAD_SHA"/],
  ["recorded exact head", /echo "head-sha=\$HEAD_SHA"/],
  ["exact PR base", /BASE_SHA="\$\{\{ github\.event\.pull_request\.base\.sha \}\}"/],
  ["base snapshot", /git cat-file -e "\$BASE_SHA:sdk\/typescript\/openapi\.snapshot\.json"/],
  ["immutable oasdiff", /tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/],
  ["v1 scope", /--match-path '\^\/api\/v1\(\?:\/\|\$\)'/],
  ["breaking-only exit", /elif \[\[ \$result -eq 1 \]\]; then/],
  ["fatal tool exit", /only exit code 1 is an approvable breaking-change result[\s\S]*exit "\$result"/],
  ["protected approval environment", /environment:\s*\n\s+name: api-breaking-change-approved/],
  ["approval secret reachability", /APPROVAL_EVIDENCE: \$\{\{ secrets\.API_BREAKING_CHANGE_EVIDENCE \}\}/],
  ["contract gate dependencies", /needs: \[contract, breaking_change_approval\]/],
];

function validateOpenApiContract(contents, invariants = openApiInvariants) {
  for (const [name, clause] of invariants) assert.match(contents, clause, `OpenAPI compatibility gate is missing ${name}`);
}

test("governance documents encode the v1 compatibility, support, security, ownership, and recovery contract", () => {
  requireClauses("docs/api-compatibility.md", [
    /at least 12 months and through at least one subsequent stable minor release/i,
    /owner, announcement date, earliest removal date\/version, and replacement\/migration/i,
    /Deprecation: true/i,
    /Sunset/i,
    /\/api\/v2/i,
    /api-breaking-change-approved/,
  ]);
  requireClauses("SECURITY.md", [
    /https:\/\/github\.com\/Syntax-Circus\/cmsify\/security\/advisories\/new/,
    /private/i,
    /do not.*secret.*public issue/i,
    /business days/i,
    /coordinated disclosure/i,
  ]);
  requireClauses("SUPPORT.md", [/Security reports/i, /Defects/i, /Usage questions/i, /Support window/i, /End of support/i]);
  requireClauses("docs/release-runbook.md", [/preflight/i, /certify/i, /promote/i, /immutable digest/i, /do not rebuild/i, /public restore/i, /unverified prerequisite/i]);
  requireClauses("docs/rollback-runbook.md", [/abort/i, /backup/i, /restore/i, /immutable digest/i, /do not rebuild/i, /public restore/i]);
  requireClauses("docs/operations.md", [/release runbook/i, /rollback runbook/i]);
  requireClauses("README.md", [/release runbook/i, /rollback runbook/i, /SECURITY\.md/i, /SUPPORT\.md/i]);
  requireClauses("docs/README.md", [/release runbook/i, /rollback runbook/i, /API compatibility/i]);
});

test("CODEOWNERS remains pending until a repository-verified GitHub owner is supplied", () => {
  const contents = requireClauses(".github/CODEOWNERS", [/pending activation/i, /verified GitHub user or team/i, /external governance gate/i]);
  assert.equal(contents.split(/\r?\n/).filter((line) => line.trim() && !line.trim().startsWith("#")).length, 0, "unverified local metadata must not invent a CODEOWNER");
});

test("governance clauses and hosted-state boundaries reject targeted contradictions", () => {
  const clauses = [
    ["docs/api-compatibility.md", /at least 12 months and through at least one subsequent stable minor release/i],
    ["SECURITY.md", /security\/advisories\/new/],
    ["SUPPORT.md", /End of support/i],
    ["docs/release-runbook.md", /`resolve` → `build` → parallel `artifact-smoke`, `candidate-accessibility`, `dotnet-consumer`, `node-consumer`, and `upgrade-rollback` → `certify` → `promote`/],
    ["docs/rollback-runbook.md", /Do not rebuild/i],
  ];
  for (const [path, clause] of clauses) {
    const documents = governanceDocuments();
    documents[path] = documents[path].replace(clause, "removed-governance-clause");
    assert.equal(validateGovernanceContract({ workflow: document(".github/workflows/openapi-contract.yml"), documents }).ok, false, `${path} mutation unexpectedly passed the real validator`);
  }
  const documents = governanceDocuments();
  documents["docs/release-runbook.md"] = documents["docs/release-runbook.md"].replace("this file does not claim they are configured", "signing is active and publication is configured");
  assert.equal(validateGovernanceContract({ workflow: document(".github/workflows/openapi-contract.yml"), documents }).ok, false, "hosted-state overclaim unexpectedly passed the real validator");
});

test("every declared document clause and hosted overclaim is checked by the shared validator", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  for (const [path, clauses] of Object.entries(governanceDocumentClauses)) for (const clause of clauses) {
    const documents = governanceDocuments();
    const changed = documents[path].replace(clause, "removed-governance-clause");
    assert.notEqual(changed, documents[path], `${path} mutation must change source`);
    documents[path] = changed;
    assert.equal(validateGovernanceContract({ workflow, documents }).ok, false, `${path} ${clause} mutation unexpectedly passed`);
  }
  for (const claim of ["GitHub environment is protected", "GitHub environments have been enabled", "environment protection is configured", "environment protections are configured", "registry permission is configured", "registry permissions have been enabled", "CODEOWNERS is active", "advisory is enabled", "advisories have been enabled", "signing has been enabled", "Cosign identity policy is active", "Cosign identity policies have been configured", "NuGet trusted publishing is configured", "npm trusted publishing has been enabled", "publication is published", "publications are configured"]) {
    const documents = governanceDocuments();
    documents["docs/release-runbook.md"] = `${documents["docs/release-runbook.md"]}\n${claim}.`;
    assert.equal(validateGovernanceContract({ workflow, documents }).ok, false, `${claim} overclaim unexpectedly passed`);
  }
});

test("exact hosted disclaimers cannot conceal affirmative claims through conjunctions or punctuation", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const claim = "signing has been enabled";
  const combinations = [
    ["conjunction", (disclaimer) => `${disclaimer.replace(/\.$/, "")}, but ${claim}.`],
    ["punctuation", (disclaimer) => `${disclaimer.replace(/\.$/, "")}; ${claim}.`],
    ["new sentence", (disclaimer) => `${disclaimer} ${claim}.`],
  ];
  for (const disclaimer of honestHostedPrerequisiteDisclaimers) for (const [form, combine] of combinations) {
    const documents = governanceDocuments();
    const changed = `${documents["docs/release-runbook.md"]}\n${combine(disclaimer)}\n`;
    assert.notEqual(changed, documents["docs/release-runbook.md"], `${form} mutation must change source`);
    documents["docs/release-runbook.md"] = changed;
    assert.equal(validateGovernanceContract({ workflow, documents }).ok, false, `${form} claim combined with ${disclaimer} unexpectedly passed`);
  }
});

test("every CODEOWNERS and exact hosted-honesty condition is checked by the shared validator", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const mutations = [
    ["pending activation", (source) => source.replace(/pending activation/i, "removed-governance-clause")],
    ["verified GitHub owner", (source) => source.replace(/verified GitHub user or team/i, "removed-governance-clause")],
    ["comment-only ownership", (source) => `${source}\n* @unverified-owner\n`],
    ...honestHostedPrerequisiteDisclaimers.map((disclaimer) => ["exact honest hosted prerequisite disclaimer", (source) => source.replace(disclaimer, "hosted configuration is complete")]),
  ];
  for (const [name, mutate] of mutations) {
    const documents = governanceDocuments();
    const path = name === "comment-only ownership" || name === "pending activation" || name === "verified GitHub owner" ? ".github/CODEOWNERS" : "docs/release-runbook.md";
    const changed = mutate(documents[path]);
    assert.notEqual(changed, documents[path], `${name} mutation must change source`);
    documents[path] = changed;
    assert.equal(validateGovernanceContract({ workflow, documents }).ok, false, `${name} mutation unexpectedly passed`);
  }
});

test("approval and gate invariants are independently fail-closed", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const mutations = [
    (source) => source.replace("needs: contract\n    if: needs.contract.outputs.breaking == 'true'", "needs: build\n    if: needs.contract.outputs.breaking == 'true'"),
    (source) => source.replace("if: needs.contract.outputs.breaking == 'true'", "if: always()"),
    (source) => source.replace("if: always()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check", "if: success()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check"),
    (source) => source.replace('if [[ "${{ needs.contract.result }}" != "success" ]]; then', "if false; then"),
    (source) => source.replace('"${{ needs.breaking_change_approval.result }}" != "success"', '"${{ needs.breaking_change_approval.result }}" == "success"'),
  ];
  for (const mutate of mutations) assert.equal(validateGovernanceContract({ workflow: mutate(workflow) }).ok, false, "approval/gate mutation unexpectedly passed");
});

test("OpenAPI compatibility comparison is exact, scoped, pinned, and fail-closed", () => {
  const contents = document(".github/workflows/openapi-contract.yml");
  validateOpenApiContract(contents);
  const mutations = [
    ["contract.checkout", (source) => source.replace("ref: ${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}", "ref: ${{ github.sha }}")],
    ["contract.revisions PR head", (source) => source.replace('HEAD_SHA="${{ github.event.pull_request.head.sha }}"', 'HEAD_SHA="${{ github.sha }}"')],
    ["contract.revisions checkout check", (source) => source.replace('test "$(git rev-parse HEAD)" = "$HEAD_SHA"', "true")],
    ["contract.revisions head record", (source) => source.replace('echo "head-sha=$HEAD_SHA"', 'echo "head-sha=${{ github.sha }}"')],
    ["contract.revisions PR base", (source) => source.replace('BASE_SHA="${{ github.event.pull_request.base.sha }}"', 'BASE_SHA="${{ github.event.pull_request.head.sha }}"')],
    ["contract.revisions base snapshot", (source) => source.replace('git cat-file -e "$BASE_SHA:sdk/typescript/openapi.snapshot.json"', "true")],
    ["contract.diff digest", (source) => source.replace(/tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/, "tufin/oasdiff:v1.28.0")],
    ["contract.diff scope", (source) => source.replace("--match-path '^/api/v1(?:/|$)'", "")],
    ["contract.diff breaking result", (source) => source.replace("elif [[ $result -eq 1 ]]; then", "else")],
    ["contract.diff fatal tool exit", (source) => source.replace('exit "$result"', "exit 0")],
    ["approval environment", (source) => source.replace("name: api-breaking-change-approved", "name: unprotected")],
    ["approval secret", (source) => source.replace("secrets.API_BREAKING_CHANGE_EVIDENCE", "github.event.inputs.approval")],
    ["contract gate dependencies", (source) => source.replace("needs: [contract, breaking_change_approval]", "needs: [contract]")],
  ];
  assert.equal(mutations.length, 13, "every declared OpenAPI invariant needs an isolated mutation");
  for (const [name, mutate] of mutations) {
    const result = validateGovernanceContract({ workflow: mutate(contents) });
    assert.equal(result.ok, false, `${name} mutation unexpectedly passed`);
  }
});
