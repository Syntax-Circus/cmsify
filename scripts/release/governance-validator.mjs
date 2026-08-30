function activeLines(source) {
  return source.replaceAll("\r\n", "\n").split("\n").filter((line) => !/^\s*#/.test(line)).map((line) => line.replace(/\s+#.*$/, ""));
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
  const aliases = lines.filter((line) => new RegExp(`^  ["']${name}["']:\\s*$`).test(line));
  if (aliases.length > 0) errors.push(`job ${name} must use one unquoted exact key`);
  return uniqueBody(lines, new RegExp(`^  ${name}:\\s*$`), /^  (?:[A-Za-z0-9_-]+|["'][A-Za-z0-9_-]+["']):\s*$/, `job ${name}`, errors);
}

function stepBody(job, id, name, errors) {
  const lines = job.split("\n");
  const aliases = lines.filter((line) => new RegExp(`^        id:\\s*["']${id}["']$`).test(line));
  if (aliases.length > 0) errors.push(`step id ${id} must use one unquoted exact key`);
  const ids = lines.flatMap((line, index) => new RegExp(`^        id:\\s*${id}$`).test(line) ? [index] : []);
  if (ids.length !== 1) {
    errors.push(`step id ${id} must occur exactly once as an active workflow field`);
    return "";
  }
  const idIndex = ids[0];
  const start = lines.findLastIndex((line, index) => index <= idIndex && /^      - /.test(line));
  const end = lines.findIndex((line, index) => index > idIndex && /^      - /.test(line));
  const body = lines.slice(start, end === -1 ? lines.length : end).join("\n");
  if (!body.startsWith(`      - name: ${name}\n`)) errors.push(`step id ${id} must retain expected operational name`);
  if (/^        if:/m.test(body) || /^        continue-on-error:/m.test(body)) errors.push(`step id ${id} must not be disabled or continue on error`);
  return body;
}

function checkoutBody(job, errors) {
  const lines = job.split("\n");
  const indexes = lines.flatMap((line, index) => /^      - uses:\s*actions\/checkout@/.test(line) ? [index] : []);
  if (indexes.length !== 1) {
    errors.push("contract checkout action must occur exactly once");
    return "";
  }
  const start = indexes[0];
  const end = lines.findIndex((line, index) => index > start && /^      - /.test(line));
  const body = lines.slice(start, end === -1 ? lines.length : end).join("\n");
  if (!/^      - uses: actions\/checkout@[0-9a-f]{40}$/m.test(body)) errors.push("contract checkout action must remain pinned by exact commit");
  if (/^        if:/m.test(body) || /^        continue-on-error:/m.test(body)) errors.push("contract checkout action must not be disabled or continue on error");
  return body;
}

function requireMatch(errors, source, expression, message) {
  if (!expression.test(source)) errors.push(message);
}

export function validateGovernanceContract({ workflow, documents = {} }) {
  const errors = [];
  const lines = activeLines(workflow);
  const contract = jobBody(lines, "contract", errors);
  const checkout = checkoutBody(contract, errors);
  const revisions = stepBody(contract, "revisions", "Resolve exact comparison revisions", errors);
  const diff = stepBody(contract, "diff", "Detect breaking /api/v1 changes with oasdiff 1.28.0", errors);
  const approval = jobBody(lines, "breaking_change_approval", errors);
  const gate = jobBody(lines, "contract-gate", errors);

  requireMatch(errors, checkout, /ref: \$\{\{ github\.event_name == 'pull_request' && github\.event\.pull_request\.head\.sha \|\| github\.sha \}\}/, "contract.checkout must use the exact PR head ref");
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

  for (const [path, clauses] of Object.entries(governanceDocumentClauses)) {
    if (documents[path] === undefined) continue;
    for (const clause of clauses) requireMatch(errors, documents[path], clause, `${path} is missing required governance policy`);
  }
  if (documents[".github/CODEOWNERS"] !== undefined) {
    const owners = documents[".github/CODEOWNERS"];
    if (!/pending activation/i.test(owners) || !/verified GitHub user or team/i.test(owners) || owners.split(/\r?\n/).some((line) => line.trim() && !line.trim().startsWith("#"))) errors.push("CODEOWNERS must remain comment-only pending verified ownership activation");
  }
  const governanceText = Object.values(documents).join("\n");
  const affirmativeHostedClaim = governanceText.split(/[.!?]/).some((sentence) => !/\b(?:must verify|unverified|do not claim|pending activation)\b/i.test(sentence) && /(?:environment protections?|registry permissions?|CODEOWNERS|advisory|advisories|signing|NuGet trusted publishing|npm trusted publishing|publications?)\s+(?:is|are|has been)\s+(?:configured|active|enabled|complete|published)/i.test(sentence));
  if (/unverified prerequisites\.[\s\S]*; they are configured/i.test(governanceText) || affirmativeHostedClaim) errors.push("Governance documents must not claim hosted protections, ownership, signing, or publication are active/configured");
  return { ok: errors.length === 0, errors };
}

export const governanceDocumentClauses = {
  "docs/api-compatibility.md": [
    /at least 12 months and through at least one subsequent stable minor release/i,
    /identifies its owner, announcement date, earliest removal date\/version, and replacement\/migration path/i,
    /Deprecation: true/,
    /absolute-date `Sunset` header/i,
    /No endpoint receives those headers unless a documented deprecation decision exists/i,
    /requires `\/api\/v2`, except for an explicitly reviewed emergency exception/i,
    /protected GitHub environment named `api-breaking-change-approved`/i,
    /non-empty `API_BREAKING_CHANGE_EVIDENCE` configured as an environment secret/i,
    /Labels, commit messages, workflow inputs, tool failures, and ordinary review never waive/i,
    /exact event PR head \(`pull_request\.head\.sha`\)/i,
    /exact target-branch base \(`pull_request\.base\.sha`\)/i,
    /oasdiff `1\.28\.0` and is scoped to `\/api\/v1`/i,
  ],
  "SECURITY.md": [
    /github\.com\/Syntax-Circus\/cmsify\/security\/advisories\/new/i,
    /Do not put vulnerabilities, reproduction data, logs, tokens, connection strings, encrypted key material, or other secrets in a public issue/i,
    /must verify that GitHub private advisories are enabled before relying on this route/i,
    /do not disclose the report publicly/i,
    /does not publish an unverified security email address/i,
    /current stable major release line is supported until an end-of-support notice says otherwise/i,
    /acknowledgement within 3 business days/i,
    /initial assessment within 10 business days/i,
    /status update at least every 10 business days/i,
    /coordinated disclosure/i,
  ],
  "SUPPORT.md": [
    /- \*\*Security reports:\*\*/i,
    /- \*\*Defects:\*\*/i,
    /- \*\*Usage questions:\*\*/i,
    /## Support window and end of support/i,
    /end-of-support notice will identify the last supported version, the final security-fix date if any, and the supported upgrade path/i,
    /do not infer a hosted SLA/i,
  ],
  "docs/release-runbook.md": [
    /release operator records the exact tag, source SHA, candidate artifact hashes, OCI manifest digests, and workflow run URL/i,
    /approver supplies protected approval evidence when a breaking `\/api\/v1` change or an emergency exception is requested/i,
    /backup custodian verifies the matched PostgreSQL, media, and Admin Data Protection-key backup manifest/i,
    /GitHub environment protection, registry permissions, npm\/NuGet trusted publishing, advisory enablement, Cosign identity policy, and CODEOWNERS activation are unverified prerequisites/i,
    /this file does not claim they are configured/i,
    /`resolve` → `build` → parallel `artifact-smoke`, `candidate-accessibility`, `dotnet-consumer`, `node-consumer`, and `upgrade-rollback` → `certify` → `promote`/i,
    /generated release manifest, `SHA256SUMS`, SPDX files, accessibility output, upgrade diagnostics, package content hashes, and each immutable digest/i,
    /Abort before promotion if any command fails/i,
    /required protected approval is absent/i,
    /backup manifest is incomplete/i,
    /public restore remains unproved/i,
    /Do not rebuild a candidate to repair evidence/i,
    /do not publish or promote without the required approval/i,
    /copy the certified OCI descriptor by digest and compare the remote digest before package publication/i,
    /must not rebuild an image/i,
    /matched database\/media backup, the retained prior image digest, `\/health\/live`, `\/health\/ready`, Admin sign-in, representative authenticated reads, and representative media downloads/i,
    /public restore gate for `SyntaxCircus\.Http\.Resilience` remains user-owned/i,
  ],
  "docs/rollback-runbook.md": [
    /Abort a rollout when readiness, authenticated reads, representative media downloads, migration behavior, backup verification, or candidate digest verification fails/i,
    /Preserve the workflow\/run output, exact source SHA, failing immutable digest, and bounded diagnostics/i,
    /Do not rebuild or replace a failed candidate/i,
    /deployed and prior API\/Admin image immutable digest values/i,
    /matched PostgreSQL, media, and Admin Data Protection-key backup manifest/i,
    /retained prior images are the recorded immutable digests/i,
    /Restore PostgreSQL and media from the same pre-upgrade backup generation/i,
    /`\/health\/live` and `\/health\/ready`/,
    /byte-for-byte representative media downloads before returning traffic/i,
    /public restore or package-replacement gate remains separate/i,
  ],
};
