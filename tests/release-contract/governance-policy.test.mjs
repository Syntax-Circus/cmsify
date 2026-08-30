import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { validateGovernanceContract } from "../../scripts/release/governance-validator.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

function document(path) {
  const fullPath = resolve(repositoryRoot, path);
  assert.equal(existsSync(fullPath), true, `${path} must exist`);
  return readFileSync(fullPath, "utf8");
}

test("real governance validator ignores decoys outside the three OpenAPI jobs", () => {
  const workflow = document(".github/workflows/openapi-contract.yml");
  const decoy = `# HEAD_SHA="${"${{ github.event.pull_request.head.sha }}"}"\n${workflow}`
    .replace('if: needs.contract.outputs.breaking == \'true\'', "if: always()")
    .replace('if: always()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check', 'if: success()\n    runs-on: ubuntu-latest\n    steps:\n      - name: Require a successful contract check');
  const result = validateGovernanceContract({ workflow: decoy });
  assert.equal(result.ok, false);
  assert.ok(result.errors.some((error) => /breaking_change_approval\.if/.test(error)));
  assert.ok(result.errors.some((error) => /contract-gate\.if/.test(error)));
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
    const contents = document(path);
    const mutated = contents.replace(clause, "removed-governance-clause");
    assert.throws(() => assert.match(mutated, clause, `${path} is missing required governance policy`), /missing required governance policy/);
  }
  const releaseRunbook = document("docs/release-runbook.md");
  const hostedOverclaim = releaseRunbook.replace("this file does not claim they are configured", "they are configured");
  assert.throws(() => assert.doesNotMatch(hostedOverclaim, /unverified prerequisites\.[\s\S]*; they are configured/i), /expected to not match/);
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
