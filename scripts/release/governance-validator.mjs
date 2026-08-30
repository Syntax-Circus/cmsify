function activeLines(source) {
  return source.replaceAll("\r\n", "\n").split("\n").filter((line) => !/^\s*#/.test(line));
}

function uniqueBody(lines, startExpression, endExpression, label, errors) {
  const indexes = lines.flatMap((line, index) => startExpression.test(line) ? [index] : []);
  if (indexes.length !== 1) {
    errors.push(`${label} must occur exactly once as an active workflow field`);
    return "";
  }
  const start = indexes[0];
  const end = lines.findIndex((line, index) => index > start && endExpression.test(line));
  return lines.slice(start, end === -1 ? lines.length : end).join("\n");
}

function jobBody(lines, name, errors) {
  return uniqueBody(lines, new RegExp(`^  ${name}:\\s*$`), /^  [A-Za-z0-9_-]+:\s*$/, `job ${name}`, errors);
}

function stepBody(job, name, errors) {
  return uniqueBody(job.split("\n"), new RegExp(`^      - name: ${name}$`), /^      - /, `step ${name}`, errors);
}

function requireMatch(errors, source, expression, message) {
  if (!expression.test(source)) errors.push(message);
}

export function validateGovernanceContract({ workflow, documents = {} }) {
  const errors = [];
  const lines = activeLines(workflow);
  const contract = jobBody(lines, "contract", errors);
  const revisions = stepBody(contract, "Resolve exact comparison revisions", errors);
  const diff = stepBody(contract, "Detect breaking /api/v1 changes with oasdiff 1.28.0", errors);
  const approval = jobBody(lines, "breaking_change_approval", errors);
  const gate = jobBody(lines, "contract-gate", errors);

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
    "docs/api-compatibility.md": [/12 months/i, /subsequent stable minor release/i, /owner/i, /announcement date/i, /earliest removal date\/version/i, /replacement\/migration/i, /Deprecation: true/, /Sunset/, /\/api\/v2/, /api-breaking-change-approved/],
    "SECURITY.md": [/security\/advisories\/new/, /supported versions/i, /do not.*secret.*public issue/i, /3 business days/i, /10 business days/i, /coordinated disclosure/i],
    "SUPPORT.md": [/Security reports/i, /Defects/i, /Usage questions/i, /Support window/i, /End of support/i],
    "docs/release-runbook.md": [/release operator/i, /approver/i, /backup custodian/i, /`resolve` → `build` → parallel/i, /SHA256SUMS/i, /Abort before promotion/i, /protected approval/i, /database\/media backup/i, /public restore/i, /do not rebuild/i, /do not publish or promote without the required approval/i, /unverified prerequisites/i],
    "docs/rollback-runbook.md": [/Abort a rollout/i, /matched PostgreSQL, media/i, /immutable digest/i, /Restore PostgreSQL and media from the same/i, /health\/live/i, /health\/ready/i, /public restore/i, /do not rebuild/i],
  };
  for (const [path, clauses] of Object.entries(requiredDocuments)) {
    if (documents[path] === undefined) continue;
    for (const clause of clauses) requireMatch(errors, documents[path], clause, `${path} is missing required governance policy`);
  }
  if (documents[".github/CODEOWNERS"] !== undefined) {
    const owners = documents[".github/CODEOWNERS"];
    if (!/pending activation/i.test(owners) || !/verified GitHub user or team/i.test(owners) || owners.split(/\r?\n/).some((line) => line.trim() && !line.trim().startsWith("#"))) errors.push("CODEOWNERS must remain comment-only pending verified ownership activation");
  }
  const governanceText = Object.values(documents).join("\n");
  if (/unverified prerequisites\.[\s\S]*; they are configured/i.test(governanceText) || /(?:environment protection|CODEOWNERS|signing|publication) (?:is|are|has been) (?:configured|active|enabled|complete)/i.test(governanceText)) errors.push("Governance documents must not claim hosted protections, ownership, signing, or publication are active/configured");
  return { ok: errors.length === 0, errors };
}
