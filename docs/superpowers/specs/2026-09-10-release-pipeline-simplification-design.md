# Release Pipeline Simplification Design

Date: 2026-09-10
Status: Approved

## Context

The release pipeline (`publish-cmsify.yml`, `upgrade-rollback.yml`, `record-release-soak.yml`, `scripts/release/*`, `eng/upgrade-tests/*`, `tests/release-contract/*`) grew to roughly 17,000 lines of workflow YAML, scripts, and tests, built around an extremely paranoid supply-chain integrity model: an offline Skopeo/OCI-loader transport proving byte-level image identity with no network or Docker socket access, a 1,100-line governance verifier that re-asserts the exact wording of the release workflow's YAML, and a moving-baseline upgrade/rollback rehearsal that hand-duplicates a large slice of the Content API's JSON contract as static assertions.

This rigor became the source of its own failures rather than protecting against real ones:

- The Skopeo image was pinned by digest against `quay.io/skopeo/stable`, a tag quay.io rotates and garbage-collects on a roughly 1-3 day cadence. This broke the release pipeline six times in nine days (five prior manual re-pin commits, then again on 2026-09-09).
- The upgrade/rollback rehearsal's assertions were never updated for the 0.4.0 content-version-unification breaking change, so the job carried `continue-on-error: true` — which itself violated a governance rule requiring that job to fail closed, breaking the entire `release-contract` test suite (20+ tests) on every PR regardless of what the PR touched.
- Getting a single patch release (`v0.4.1` → `v0.4.2`) out required investigating and fixing both of the above, plus a stale unit-test mock, before a real release could complete — for a pre-1.0 project with no real external consumers or production installs yet.

The user made an explicit, informed call: this project doesn't currently need this level of rigor, and the rigor's maintenance cost has exceeded its value. The direction is fail-forward, not rollback-obsessed - trade some supply-chain ceremony for a pipeline that's small enough to read start to finish and reason about directly, keeping only the pieces that provide real signal without being a maintenance burden (Cosign signing, SBOM generation, a manual approval gate before publish) and cutting everything that exists primarily to protect against its own complexity (the offline OCI-loader transport, the governance verifier, the upgrade/rollback rehearsal, soak recording, and the isolated clean-install consumer checks).

## Decision

Replace `publish-cmsify.yml` with a four-job pipeline of roughly 80-100 lines, and delete everything that exists solely to support the machinery being removed.

### New pipeline shape

Triggered on `push: tags: ["v*"]`, same as today.

1. **`resolve`** - validate the tag (`scripts/release/validate-release-tag.mjs`, kept as-is - it's a small, self-contained SemVer/changelog check, not a source of brittleness), emit `version`/`source_sha` outputs. Unchanged from the current job in shape and size.
2. **`build-test-publish-images`** - checkout at `source_sha`, restore/build/test the .NET solution, pack the five NuGet packages and the npm SDK package (unchanged steps, copied from the current `build` job), then `docker buildx build --push` both images (API, Admin) tagged `docker.io/syntaxcircus/cmsify-{api,admin}:$VERSION` directly to Docker Hub - no OCI-archive artifact, no offline loader, no Skopeo. Immediately after each push, smoke-test by running the pushed image directly (`docker run -d --pull always <image>`) and polling `/health/live` and `/health/ready` with `curl --retry`. If either image fails its smoke test, the job fails and nothing downstream runs.
3. **`sign-and-attest`** (needs `build-test-publish-images`) - `cosign sign --yes` both images by digest (keeps the existing OIDC keyless-signing setup), `syft` an SBOM for each image, upload both SBOMs as workflow artifacts for the next job to attach to the release.
4. **`promote`** (needs `sign-and-attest`, `environment: release` - same manual approval gate as today) - `dotnet nuget push` the five packages and `npm publish` the SDK package (unchanged trusted-publishing/OIDC steps, copied from the current `promote` job), `gh release create` attaching the SBOMs, using the existing changelog-excerpt-into-release-notes logic.

No OCI-layout artifact is produced or downloaded between jobs; `build-test-publish-images` pushes directly to the registry it already needs to push to, and downstream jobs reference images by the digest that job outputs.

### What is deleted entirely

- `scripts/release/load-oci-candidate.mjs` and its test `tests/release-contract/load-oci-candidate.test.mjs` (879 lines) - the offline OCI-loader transport this replaces.
- `.github/workflows/mirror-skopeo.yml` - existed only to keep the OCI loader's Skopeo pin off quay.io; nothing needs Skopeo once the loader is gone.
- `.github/workflows/upgrade-rollback.yml`, `eng/upgrade-tests/` (rehearsal.mjs, assertions.mjs, fixture.mjs, docker.mjs, http.mjs, cli.mjs, release-baseline.mjs, process.mjs, expected.mjs, manifest.mjs), `tests/upgrade/` (fixtures, compose files, the v0.1.3 database dump, media fixtures, README) - the entire upgrade/rollback rehearsal.
- `.github/workflows/record-release-soak.yml` - soak recording for a job (`upgrade-rollback`) that no longer exists.
- `scripts/release/verify-release-contract.mjs`, `scripts/release/governance-validator.mjs`, and everything under `tests/release-contract/` (quality-policy, capacity-report, task-12-external-gates, coverage-summary, quality-evidence-manifest, quality-documentation, governance-policy, verify-release-artifacts - roughly 7,000 lines) - the governance verifier and its own test suite.
- The `dotnet-consumer` and `node-consumer` jobs and their supporting steps in `publish-cmsify.yml`.
- The `candidate-accessibility` job in `publish-cmsify.yml`. Accessibility scanning of the Admin UI continues to run on every PR via the pre-existing, independent `admin-accessibility.yml` workflow - this only removes it as a release-blocking gate, it does not remove accessibility testing from the project.
- `docs/evidence/task-12-local-verification.json` and any other generated evidence files that only existed to feed the deleted governance verifier.

### What is kept, unchanged

- `scripts/release/validate-release-tag.mjs` and its changelog-presence check.
- Cosign signing and SBOM generation (moved into the new `sign-and-attest` job, logic unchanged).
- The `environment: release` manual approval gate before `promote`.
- npm/NuGet trusted publishing (OIDC) - untouched, was never a source of the brittleness.
- `.github/workflows/admin-accessibility.yml`, `openapi-contract.yml`, `dotnet-test.yml`, `capacity-trends.yml`, `typescript-sdk.yml` - none of these are part of the release pipeline; out of scope for this change.

### Documentation

- `docs/release-runbook.md` and `docs/rollback-runbook.md` are rewritten to describe the four-job flow above in a few paragraphs, replacing their current content (which describes the offline loader, governance verifier evidence ledger, and soak recording procedures - all being deleted).
- `AGENTS.md`/`CLAUDE.md` are checked for references to deleted scripts/workflows and updated.
- Historical design docs under `docs/superpowers/specs/` (including `2026-08-30-offline-oci-loader-transport-design.md`) are left as-is - they are dated records of a past decision, not live documentation, and this spec supersedes that decision rather than erasing its history.

## Verification

Since the deleted governance verifier can no longer be relied on to validate the new workflow, and there is no lower-stakes environment to rehearse a real tag push against, verification is:

1. `actionlint` (or equivalent YAML/expression linting) against the new `publish-cmsify.yml` to catch syntax and expression errors before ever pushing a tag.
2. A full local dry run of the buildable pieces: `dotnet build`/`test`, NuGet pack, npm pack, and a local `docker build` + `docker run` + health-check smoke test of both images, run by hand exactly as the workflow will run them.
3. A real tag push (the next real patch version) is the actual end-to-end test, since this is a release pipeline and there is no meaningful way to fully rehearse it offline. The manual approval gate before `promote` remains the safety net if something is wrong that only surfaces at that stage - nothing publishes without an explicit human approval.

## Out of Scope

- Any change to the domain code, API, or Admin UI.
- Any change to PR-time CI (`dotnet-test.yml`, `openapi-contract.yml`, `admin-accessibility.yml`, `capacity-trends.yml`, `typescript-sdk.yml`).
- Reintroducing any form of offline/air-gapped image transport, moving-baseline upgrade testing, or hand-written API-contract duplication if a real need for them resurfaces later - that would be a new, separately-justified decision, not a default to return to.
