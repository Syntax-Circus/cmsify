function activeLines(source) {
  return source.replaceAll("\r\n", "\n").split("\n").filter((line) => !/^\s*#/.test(line)).map((line) => line.replace(/\s+#.*$/, "").trimEnd());
}

function uniqueBody(lines, startExpression, endExpression, label, errors) {
  const indexes = lines.flatMap((line, index) => startExpression.test(line) ? [index] : []);
  if (indexes.length !== 1) {
    errors.push(`${label} must occur exactly once as an active workflow field`);
    return "";
  }
  const start = indexes[0];
  const end = lines.findIndex((line, index) => index > start && endExpression.test(line));
  return lines.slice(start, end === -1 ? lines.length : end).join("\n").trimEnd();
}

function jobBody(lines, name, errors) {
  const aliases = lines.filter((line) => new RegExp(`^  ["']${name}["']:\\s*$`).test(line));
  if (aliases.length > 0) errors.push(`job ${name} must use one unquoted exact key`);
  return uniqueBody(lines, new RegExp(`^  ${name}:\\s*$`), /^  (?:[A-Za-z0-9_-]+|["'][A-Za-z0-9_-]+["']):\s*$/, `job ${name}`, errors).split("\n").slice(1).join("\n");
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
  const body = lines.slice(start, end === -1 ? lines.length : end).join("\n").trimEnd();
  if (!body.startsWith(`      - name: ${name}\n`)) errors.push(`step id ${id} must retain expected operational name`);
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
  return lines.slice(start, end === -1 ? lines.length : end).join("\n").trimEnd();
}

function contractStepSequence(job, errors) {
  const lines = job.split("\n");
  const stepHeaders = lines.filter((line) => /^    steps:\s*$/.test(line));
  if (stepHeaders.length !== 1) errors.push("contract.steps must occur exactly once as an active workflow field");
  return lines.filter((line) => /^      -(?:\s.*)?$/.test(line));
}

function requireExact(errors, source, expected, message) {
  if (source !== expected) errors.push(message);
}

const canonicalCheckout = `      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683
        with:
          fetch-depth: 0
          ref: \${{ github.event_name == 'pull_request' && github.event.pull_request.head.sha || github.sha }}`;

const canonicalContractStepSequence = [
  "      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683",
  "      - uses: actions/setup-dotnet@d4c94342e560b34958eacfc5d055d21461ed1c5d",
  "      - uses: actions/setup-node@a0853c24544627f65ddf259abe73b1d18a591444",
  "      - name: Install TypeScript generator",
  "      - name: Restore locked solution dependencies",
  "      - name: Restore pinned OpenAPI exporter",
  "      - name: Resolve exact comparison revisions",
  "      - name: Verify live OpenAPI and tracked generated output",
  "      - name: Export live head document",
  "      - name: Materialize target-branch contract",
  "      - name: Detect breaking /api/v1 changes with oasdiff 1.28.0",
  "      - name: Publish comparison evidence",
];

const canonicalRevisions = `      - name: Resolve exact comparison revisions
        id: revisions
        shell: bash
        run: |
          if [[ "\${{ github.event_name }}" == "pull_request" ]]; then
            HEAD_SHA="\${{ github.event.pull_request.head.sha }}"
            BASE_SHA="\${{ github.event.pull_request.base.sha }}"
          else
            HEAD_SHA="\${{ github.sha }}"
            BASE_SHA="\${{ github.event.before }}"
            if [[ "$BASE_SHA" == "0000000000000000000000000000000000000000" ]]; then
              BASE_SHA="$(git rev-parse "$HEAD_SHA^")"
            fi
          fi
          git cat-file -e "$HEAD_SHA^{commit}"
          test "$(git rev-parse HEAD)" = "$HEAD_SHA"
          git cat-file -e "$BASE_SHA:sdk/typescript/openapi.snapshot.json"
          echo "base-sha=$BASE_SHA" >> "$GITHUB_OUTPUT"
          echo "head-sha=$HEAD_SHA" >> "$GITHUB_OUTPUT"`;

const canonicalDiff = `      - name: Detect breaking /api/v1 changes with oasdiff 1.28.0
        id: diff
        shell: bash
        run: |
          set +e
          docker run --rm -v "$RUNNER_TEMP:/work:ro" tufin/oasdiff:v1.28.0@sha256:86830f988eaafcf589acb2794ee5ab78e3300ded071d6517bf085469300cbf36 breaking /work/openapi-base.json /work/openapi-head.json --match-path '^/api/v1(?:/|$)' --fail-on ERR > "$RUNNER_TEMP/oasdiff.txt" 2>&1
          result=$?
          cat "$RUNNER_TEMP/oasdiff.txt"
          if [[ $result -eq 0 ]]; then
            echo "breaking=false" >> "$GITHUB_OUTPUT"
          elif [[ $result -eq 1 ]]; then
            echo "breaking=true" >> "$GITHUB_OUTPUT"
          else
            echo "oasdiff failed with exit code $result; only exit code 1 is an approvable breaking-change result." >&2
            exit "$result"
          fi
          exit 0`;

const canonicalApproval = `    needs: contract
    if: needs.contract.outputs.breaking == 'true'
    runs-on: ubuntu-latest
    environment:
      name: api-breaking-change-approved
    steps:
      - name: Require protected approval evidence
        shell: bash
        env:
          APPROVAL_EVIDENCE: \${{ secrets.API_BREAKING_CHANGE_EVIDENCE }}
        run: |
          test -n "$APPROVAL_EVIDENCE"
          {
            echo "## Approved breaking API change"
            echo
            echo "- Protected environment: \\\`api-breaking-change-approved\\\`"
            echo "- Compared target/base: \\\`\${{ needs.contract.outputs.base-sha }}\\\`"
            echo "- Compared head: \\\`\${{ needs.contract.outputs.head-sha }}\\\`"
            echo "- Approval evidence: supplied through the protected environment secret."
          } >> "$GITHUB_STEP_SUMMARY"`;

const canonicalGate = `    needs: [contract, breaking_change_approval]
    if: always()
    runs-on: ubuntu-latest
    steps:
      - name: Require a successful contract check and protected approval when needed
        shell: bash
        run: |
          if [[ "\${{ needs.contract.result }}" != "success" ]]; then
            echo "The OpenAPI contract job did not succeed." >&2
            exit 1
          fi
          if [[ "\${{ needs.contract.outputs.breaking }}" == "true" && "\${{ needs.breaking_change_approval.result }}" != "success" ]]; then
            echo "A breaking /api/v1 change requires successful protected approval evidence." >&2
            exit 1
          fi`;

export function validateGovernanceContract({ workflow, documents = {} }) {
  const errors = [];
  const lines = activeLines(workflow);
  const contract = jobBody(lines, "contract", errors);
  const checkout = checkoutBody(contract, errors);
  const contractSteps = contractStepSequence(contract, errors);
  const revisions = stepBody(contract, "revisions", "Resolve exact comparison revisions", errors);
  const diff = stepBody(contract, "diff", "Detect breaking /api/v1 changes with oasdiff 1.28.0", errors);
  const approval = jobBody(lines, "breaking_change_approval", errors);
  const gate = jobBody(lines, "contract-gate", errors);

  requireExact(errors, checkout, canonicalCheckout, "contract.checkout must use the exact PR head ref in the canonical active checkout step");
  if (contractSteps.length !== canonicalContractStepSequence.length || contractSteps.some((step, index) => step !== canonicalContractStepSequence[index])) errors.push("contract.steps must retain the exact ordered canonical step sequence without extra or unrecognized steps");
  requireExact(errors, revisions, canonicalRevisions, "contract.revisions must record exact base and head identities in the canonical active revisions step");
  requireExact(errors, diff, canonicalDiff, "contract.diff must scope comparison to /api/v1 and treat only exit 1 as breaking while tool failures remain fatal in the canonical active oasdiff step");
  requireExact(errors, approval, canonicalApproval, "breaking_change_approval must be the canonical active approval job");
  requireExact(errors, gate, canonicalGate, "contract-gate must be the canonical active gate job");

  for (const [path, clauses] of Object.entries(governanceDocumentClauses)) {
    if (documents[path] === undefined) continue;
    for (const clause of clauses) if (typeof clause === "string" ? !documents[path].includes(clause) : !clause.test(documents[path])) errors.push(`${path} is missing required governance policy`);
  }
  if (documents[".github/CODEOWNERS"] !== undefined) {
    const owners = documents[".github/CODEOWNERS"];
    if (!/pending activation/i.test(owners) || !/verified GitHub user or team/i.test(owners) || owners.split(/\r?\n/).some((line) => line.trim() && !line.trim().startsWith("#"))) errors.push("CODEOWNERS must remain comment-only pending verified ownership activation");
  }
  const governanceText = Object.values(documents).join("\n");
  const exactHostedClaimDisclaimers = [
    ...honestHostedPrerequisiteDisclaimers,
    "The repository administrator must verify that GitHub private advisories are enabled before relying on this route.",
  ];
  const hostedClaimScanText = exactHostedClaimDisclaimers.reduce((text, disclaimer) => text.replaceAll(disclaimer, ""), governanceText);
  const affirmativeHostedClaim = /(?:GitHub environments?|environment protections?|registry permissions?|CODEOWNERS|advisory|advisories|signing|Cosign identity (?:policy|policies)|NuGet trusted publishing|npm trusted publishing|publications?)\s+(?:is|are|has been|have been)\s+(?:configured|active|enabled|complete|protected|published)/i.test(hostedClaimScanText);
  if (/unverified prerequisites\.[\s\S]*; they are configured/i.test(governanceText) || affirmativeHostedClaim) errors.push("Governance documents must not claim hosted protections, ownership, signing, or publication are active/configured");
  return { ok: errors.length === 0, errors };
}

export const honestHostedPrerequisiteDisclaimers = [
  "GitHub environment protection, registry permissions, npm/NuGet trusted publishing, advisory enablement, Cosign identity policy, and CODEOWNERS activation are unverified prerequisites.",
  "A repository administrator must verify them in the hosted systems before a release; this file does not claim they are configured.",
];

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
    ...honestHostedPrerequisiteDisclaimers,
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
