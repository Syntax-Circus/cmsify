# Pre-publication Whole-Branch Review Remediation Plan

> **Execution:** Use subagent-driven development with strict RED/GREEN evidence and independent review for each task.

**Goal:** Close every repository-fixable preliminary whole-branch review finding before the exact public resilience package and final candidate tuple become available.

**Base:** `4d9da511303e646c5f4147f51108bf3d87c4bba0`

**Constraints:** Keep all hosted/public/release gates false. Do not publish, push, tag, sign, attest, promote, or release. Do not claim that stubbed external-gate tests are hosted evidence. Preserve the offline loader's certified-manifest versus runtime-config identity boundary.

## Task 1: Prove complete public restore and attestation subjects

**Files:**
- Modify `scripts/release/verify-task-12-external-gate.ps1`
- Modify `tests/release-contract/task-12-external-gates.test.mjs`
- Modify `tests/release-contract/fixtures/task-12-tool-stub.mjs` only if deterministic download/restore side effects require it

### Public restore contract

- Add behavioral RED tests proving a plain successful `dotnet restore`, global package cache, HTTP cache, ignored local feed, or wrong public nupkg bytes cannot pass the gate.
- Read the expected package ID, version, SHA-256, and NuGet content hash from tracked `docs/evidence/task-12-local-verification.json`; do not duplicate a second uncontrolled identity.
- Create one exact run-owned non-linked temporary root and a minimal NuGet configuration with `<clear />` plus only `https://api.nuget.org/v3/index.json`.
- Download the exact flat-container nupkg into that root without local/cache fallback and require its SHA-256 to equal the tracked value.
- Restore `Cmsify.slnx` with the generated config, a fresh exact packages directory, `--no-http-cache`, and `--locked-mode`.
- Verify exactly five affected `project.assets.json` graphs were produced by that restore and resolve the exact library as `type: package`, exact package path, and exact content hash; reject sibling/project identity.
- Cleanup only the exact run-owned temporary root on success and failure.

### Attestation contract

- Replace the arbitrary nonempty checksum fixture with a complete canonical release-candidate fixture.
- Before invoking `gh attestation verify`, parse `release-manifest.json`, require its version/source SHA to match gate inputs, and run `scripts/release/verify-release-artifacts.mjs` against the candidate root.
- Require `SHA256SUMS` to equal the complete canonical verified candidate subject set. Preserve link/reparse, traversal, duplicate, missing, and hash checks.
- Add one-at-a-time omission/extra/wrong-manifest mutations and prove that no attestation command runs for an invalid candidate.
- Verify every canonical checksummed subject against the exact repository, signer workflow, and source SHA.

### Validation and commit

Run focused external-gate tests, all release-contract tests, `node --check` for changed JavaScript, PowerShell parse validation, the standalone semantic verifier, and `git diff --check`.

Commit: `Prove complete external release evidence`

## Task 2: Separate smoke artifact identity from Docker identity

**Files:**
- Modify `eng/release-smoke/cli.mjs`, `eng/release-smoke/harness.mjs`, and `eng/release-smoke/evidence.mjs`
- Modify owning `tests/release-smoke/*.test.mjs` and `tests/release-smoke/README.md`
- Modify `.github/workflows/publish-cmsify.yml`
- Modify semantic workflow tests/verifier only as required
- Remove trailing whitespace in `docs/superpowers/specs/2026-08-30-offline-oci-loader-transport-design.md`

### Identity contract

- Add required CLI inputs `--api-manifest-digest` and `--admin-manifest-digest`, each an exact lowercase `sha256:` digest. The release workflow extracts them from the already-checksummed `release-manifest.json` and passes them to the smoke CLI.
- Rename smoke evidence candidate field `digest` to `manifestDigest`; keep `imageId` as the Docker runtime config/image identity.
- `inspectCandidate` must never derive certified identity from `RepoDigests` or fall back from manifest digest to image ID. It validates runtime image ID/platform/version/revision and records the separately supplied certified manifest digest.
- Add RED/GREEN tests for absent, stale, unrelated, matching, and multiple `RepoDigests`; evidence must remain bound only to the supplied manifest digest and exact image ID.
- Update README/workflow policy mutations so omitting/swapping either manifest digest fails.

### Bounded accessibility logs

- Replace unbounded candidate-accessibility `docker logs` with a fixed line tail and byte cap while preserving cleanup status.
- Add workflow policy tests for missing tail/byte bounds and ensure secrets/payloads are not promoted into evidence.

### Validation and commit

Run all release-smoke tests, accessibility policy tests, release-contract tests, standalone verifier, syntax checks, and `git diff --check`.

Commit: `Bind smoke evidence to certified manifests`

## Task 3: Refresh preliminary tracked readiness truthfully

**Files:**
- Modify `docs/evidence/task-12-local-verification.json`
- Modify `tests/release-contract/task-12-evidence.test.mjs`
- Modify `docs/v1-release-readiness.md`
- Modify `docs/v1-release-remediation-handoff.md`
- Modify closest governance/readiness tests if required

### Evidence contract

- Bind the preliminary local source/policy tuple to the accepted implementation SHA immediately before this evidence-only task, not to the evidence commit itself.
- Record current exact source-level counts and completed repository work, including offline-loader live certification, while labeling the tuple preliminary/non-certifying.
- Update all F-01 through F-19 statuses and the handoff resume point so completed repository work is not described as future work.
- Keep exact resilience public restore, definitive package/OCI tuple, final consumers/accessibility/upgrade/smoke, hosted accessibility, approvals, attestations, signing, promotion, soak, tag, and release gates explicitly false with exact next commands/owners.
- Do not convert local or stubbed evidence into hosted/public claims.

### Validation and commit

Run evidence/governance mutation tests, all release-contract and upgrade-unit tests, the standalone verifier, link checks if present, and `git diff --check`.

Commit: `Refresh preliminary v1 readiness evidence`

## Completion gate

- Obtain an independent clean review for Tasks 1–3 and a clean preliminary whole-branch re-review.
- Rerun warning-free Release build, full .NET solution tests, TypeScript/OpenAPI checks, and the full non-live Node gate if any implementation changed.
- Recheck official NuGet availability. If exact `0.2.0-cmsify.1` is still absent, leave the overall goal active at the external publication gate; otherwise proceed immediately to the definitive candidate tuple.
