function jobBody(workflow, name) {
  const start = workflow.search(new RegExp(`^  ${name}:\\s*$`, "m"));
  if (start === -1) return "";
  const following = workflow.slice(start + 1);
  const next = following.search(/^  [A-Za-z0-9_-]+:\s*$/m);
  return next === -1 ? workflow.slice(start) : workflow.slice(start, start + 1 + next);
}

function stepBody(job, name) {
  const start = job.indexOf(`      - name: ${name}`);
  if (start === -1) return "";
  const following = job.slice(start + 1);
  const next = following.search(/^      - /m);
  return next === -1 ? job.slice(start) : job.slice(start, start + 1 + next);
}

function requireMatch(errors, source, expression, message) {
  if (!expression.test(source)) errors.push(message);
}

export function validateGovernanceContract({ workflow, documents = {} }) {
  const errors = [];
  const contract = jobBody(workflow, "contract");
  const revisions = stepBody(contract, "Resolve exact comparison revisions");
  const diff = stepBody(contract, "Detect breaking /api/v1 changes with oasdiff 1.28.0");
  const approval = jobBody(workflow, "breaking_change_approval");
  const gate = jobBody(workflow, "contract-gate");

  requireMatch(errors, contract, /ref: \$\{\{ github\.event_name == 'pull_request' && github\.event\.pull_request\.head\.sha \|\| github\.sha \}\}/, "contract.checkout must use the exact PR head ref");
  requireMatch(errors, revisions, /HEAD_SHA="\$\{\{ github\.event\.pull_request\.head\.sha \}\}"/, "contract.revisions must bind the event PR head");
  requireMatch(errors, revisions, /BASE_SHA="\$\{\{ github\.event\.pull_request\.base\.sha \}\}"/, "contract.revisions must bind the exact PR base");
  requireMatch(errors, revisions, /git cat-file -e "\$HEAD_SHA\^\{commit\}"/, "contract.revisions must verify the head commit");
  requireMatch(errors, revisions, /test "\$\(git rev-parse HEAD\)" = "\$HEAD_SHA"/, "contract.revisions must verify checkout identity");
  requireMatch(errors, revisions, /git cat-file -e "\$BASE_SHA:sdk\/typescript\/openapi\.snapshot\.json"/, "contract.revisions must materialize the exact base snapshot");
  requireMatch(errors, revisions, /echo "base-sha=\$BASE_SHA"[\s\S]*echo "head-sha=\$HEAD_SHA"/, "contract.revisions must record exact base and head identities");
  requireMatch(errors, diff, /tufin\/oasdiff:v1\.28\.0@sha256:[0-9a-f]{64}/i, "contract.diff must pin oasdiff by immutable digest");
  requireMatch(errors, diff, /--match-path '\^\/api\/v1\(\?:\/\|\$\)'/, "contract.diff must scope comparison to /api/v1");
  requireMatch(errors, diff, /elif \[\[ \$result -eq 1 \]\]; then[\s\S]*else[\s\S]*exit "\$result"/, "contract.diff must treat only exit 1 as breaking and tool failures as fatal");
  requireMatch(errors, approval, /^    needs: contract$/m, "breaking_change_approval.needs must be contract");
  requireMatch(errors, approval, /^    if: needs\.contract\.outputs\.breaking == 'true'$/m, "breaking_change_approval.if must require an exact breaking result");
  requireMatch(errors, approval, /^      name: api-breaking-change-approved$/m, "breaking_change_approval.environment must be protected");
  requireMatch(errors, approval, /APPROVAL_EVIDENCE: \$\{\{ secrets\.API_BREAKING_CHANGE_EVIDENCE \}\}[\s\S]*test -n "\$APPROVAL_EVIDENCE"/, "breaking_change_approval must reach and require the protected evidence secret");
  requireMatch(errors, gate, /^    needs: \[contract, breaking_change_approval\]$/m, "contract-gate.needs must include contract and approval");
  requireMatch(errors, gate, /^    if: always\(\)$/m, "contract-gate.if must be exactly always()");
  requireMatch(errors, gate, /needs\.contract\.result \}\}" != "success"[\s\S]*exit 1/, "contract-gate must fail when contract is not successful");
  requireMatch(errors, gate, /needs\.contract\.outputs\.breaking \}\}" == "true" && "\$\{\{ needs\.breaking_change_approval\.result \}\}" != "success"[\s\S]*exit 1/, "contract-gate must require successful approval for breaking changes");

  const requiredDocuments = {
    "docs/api-compatibility.md": [/12 months/i, /subsequent stable minor release/i, /Deprecation: true/, /Sunset/, /\/api\/v2/, /api-breaking-change-approved/],
    "SECURITY.md": [/security\/advisories\/new/, /do not.*secret.*public issue/i, /business days/i, /coordinated disclosure/i],
    "SUPPORT.md": [/Security reports/i, /Defects/i, /Usage questions/i, /Support window/i, /End of support/i],
    "docs/release-runbook.md": [/`resolve` → `build` → parallel/i, /immutable digest/i, /do not rebuild/i, /unverified prerequisites/i],
    "docs/rollback-runbook.md": [/abort/i, /backup/i, /restore/i, /immutable digest/i, /do not rebuild/i],
  };
  for (const [path, clauses] of Object.entries(requiredDocuments)) {
    if (documents[path] === undefined) continue;
    for (const clause of clauses) requireMatch(errors, documents[path], clause, `${path} is missing required governance policy`);
  }
  if (documents[".github/CODEOWNERS"] !== undefined) {
    const owners = documents[".github/CODEOWNERS"];
    if (!/pending activation/i.test(owners) || !/verified GitHub user or team/i.test(owners) || owners.split(/\r?\n/).some((line) => line.trim() && !line.trim().startsWith("#"))) errors.push("CODEOWNERS must remain comment-only pending verified ownership activation");
  }
  if (/unverified prerequisites\.[\s\S]*; they are configured/i.test(documents["docs/release-runbook.md"] ?? "")) errors.push("Release runbook must not claim hosted protections are configured");
  return { ok: errors.length === 0, errors };
}
