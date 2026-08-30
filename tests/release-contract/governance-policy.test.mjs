import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

function document(path) {
  const fullPath = resolve(repositoryRoot, path);
  assert.equal(existsSync(fullPath), true, `${path} must exist`);
  return readFileSync(fullPath, "utf8");
}

function requireClauses(path, clauses) {
  const contents = document(path);
  for (const clause of clauses) assert.match(contents, clause, `${path} is missing required governance policy`);
  return contents;
}

function validateOpenApiContract(contents) {
  for (const clause of [
    /github\.event\.pull_request\.base\.sha/,
    /echo "head-sha=\$\{\{ github\.sha \}\}"/,
    /git cat-file -e "\$base_sha:sdk\/typescript\/openapi\.snapshot\.json"/,
    /tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/,
    /--match-path '\^\/api\/v1\(\?:\/\|\$\)'/,
    /elif \[\[ \$result -eq 1 \]\]; then/,
    /only exit code 1 is an approvable breaking-change result/,
    /environment:\s*\n\s+name: api-breaking-change-approved/,
  ]) assert.match(contents, clause, "OpenAPI compatibility gate is missing required fail-closed evidence");
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

test("OpenAPI compatibility comparison is exact, scoped, pinned, and fail-closed", () => {
  const contents = document(".github/workflows/openapi-contract.yml");
  validateOpenApiContract(contents);
  for (const mutation of [
    (source) => source.replace(/tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/, "tufin/oasdiff:v1.28.0"),
    (source) => source.replace("--match-path '^/api/v1(?:/|$)'", ""),
    (source) => source.replace("elif [[ $result -eq 1 ]]; then", "else"),
  ]) assert.throws(() => validateOpenApiContract(mutation(contents)), /OpenAPI compatibility gate/);
});
