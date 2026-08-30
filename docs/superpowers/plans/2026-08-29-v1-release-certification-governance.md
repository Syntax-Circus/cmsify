# v1 Release Certification and Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the repository-owned portion of remediation Task 12 by certifying exact release candidates, closing retained reliability races, enforcing supply-chain and compatibility policy, and adding the governance and evidence needed for an honest v1 go/no-go decision.

**Architecture:** Keep the existing tag-only build-once/promote-once workflow. Move production-like candidate checks into deterministic Node harnesses with unit-tested orchestration, make workflows thin callers of those harnesses, and enforce every workflow/policy invariant through release-contract tests. Separate local repository certification from user-owned publication, protected-environment approval, signing, registry promotion, and hosted evidence so unavailable external gates remain explicitly open rather than being simulated.

**Tech Stack:** .NET 10/C# 14, ASP.NET Core, EF Core/PostgreSQL 17, Docker Buildx/OCI, Node.js 22 ESM and `node:test`, GitHub Actions, SPDX 2.3, GitHub artifact attestations, Cosign keyless signing, axe-core CLI.

**Spec:** `docs/superpowers/plans/2026-08-24-v1-remediation.md` (Task 12 and Completion Gate), with resume constraints in `docs/v1-release-remediation-handoff.md`.

## Global Constraints

- Do not redo or weaken remediation Tasks 1–11.
- Do not push, merge, tag, publish, promote, sign a public artifact, or create a release without explicit user approval.
- Consume `SyntaxCircus.Http.Resilience` exactly at `0.2.0-cmsify.1` from the ignored local feed until the user publishes those exact bytes or approves an exact stable replacement.
- Ordinary public/CI restore is a user-owned open gate until that publication or replacement occurs; no local evidence may claim it passed.
- Build NuGet, npm, and OCI candidates once from one validated tag, SemVer, and source SHA; all later jobs must download, checksum, inspect, test, attest, and promote those exact bytes without rebuilding.
- Server artifacts use `AGPL-3.0-or-later`; .NET and TypeScript SDK artifacts use `MIT`.
- Support .NET 10 and Node 20/22 consumers.
- All third-party GitHub Actions must use immutable 40-hex commit SHAs with a human-readable version comment.
- All production/runtime container references used by Dockerfiles, smoke, OpenAPI comparison, and upgrade/release workflows must use immutable `sha256:` digests while retaining the reviewed tag in the reference or adjacent comment.
- Production code and bug fixes follow red-green-refactor. Configuration/policy changes require mutation tests that fail before the policy is changed.
- Every failure artifact must be bounded, sanitized, and uploaded only on failure; credentials, tokens, ciphertext, database passwords, and webhook secrets must never enter evidence.
- Final evidence must distinguish local verification, hosted CI evidence, protected approvals, and public-registry evidence.

---

### Task 1: Enforce repository-wide immutable supply-chain inputs

**Files:**
- Create: `tests/release-contract/repository-supply-chain.test.mjs`
- Modify: `scripts/release/verify-release-contract.mjs`
- Modify: `tests/release-contract/verify-release-contract.test.mjs`
- Modify: `tests/release-contract/quality-policy.test.mjs`
- Modify: `.github/workflows/*.yml`
- Modify: `src/Cmsify.Api/Dockerfile`
- Modify: `src/Cmsify.Admin/Dockerfile`
- Modify as discovered by the policy test: tracked Docker Compose and upgrade fixture manifests containing runtime image references

**Interfaces:**
- Produces `validateRepositorySupplyChain(repositoryRoot)` in `scripts/release/verify-release-contract.mjs`, or an equivalently exported helper, that rejects floating action refs and mutable runtime image refs with file/line evidence.
- Requires action refs to match `owner/repository@<40 lowercase-or-uppercase hex characters>`; local `./` actions are exempt.
- Requires external runtime image refs to contain `@sha256:<64 hex characters>`; Docker stage aliases and references to images built earlier in the same workflow are exempt.
- Preserves SDK stage tag `10.0.400` and ASP.NET runtime major tag `10.0` before each digest so update intent remains readable.

- [ ] **Step 1: Write failing repository policy tests**

  Add tests that enumerate every tracked `.github/workflows/*.yml`, both production Dockerfiles, `tests/upgrade/compose.yml`, and tracked compose manifests. Assert that current floating `actions/*@v4`/`@v5`, mutable `mcr.microsoft.com/dotnet/*`, `postgres:17-alpine`, and `tufin/oasdiff:v1.28.0` mutations fail with the owning file and line. Add controls proving local actions, build-stage aliases, and exact candidate images produced in the same workflow remain valid.

- [ ] **Step 2: Run the tests and verify RED**

  Run: `node --test tests/release-contract/repository-supply-chain.test.mjs tests/release-contract/verify-release-contract.test.mjs tests/release-contract/quality-policy.test.mjs`

  Expected: FAIL because floating action tags and mutable runtime image references are still present.

- [ ] **Step 3: Implement the semantic validator and pin every input**

  Replace every third-party action tag in every workflow with a reviewed 40-character commit SHA plus version comment. Pin external runtime images by digest in Dockerfiles, workflows, and compose/fixture inputs. Update existing Dockerfile/restore policy parsing to recognize `tag@sha256:digest AS stage` without weakening its exact SDK, locked-restore, or stage-order checks.

- [ ] **Step 4: Verify GREEN and mutation resistance**

  Run the Step 2 command, then `node scripts/release/verify-release-contract.mjs`.

  Expected: all tests PASS; the verifier prints its existing success message and rejects one-character action/digest mutations in fixtures.

- [ ] **Step 5: Verify both production Dockerfiles still build from the pinned inputs**

  Run: `docker build --file src/Cmsify.Api/Dockerfile --tag cmsify-task12-api:local .`

  Run: `docker build --file src/Cmsify.Admin/Dockerfile --tag cmsify-task12-admin:local .`

  Expected: both builds succeed using locked restores and the exact pinned base images.

- [ ] **Step 6: Commit**

  Commit message: `Pin repository supply-chain inputs`

---

### Task 2: Make the retained media concurrency scenarios deterministic

**Files:**
- Modify: `tests/Cmsify.Api.Integration.Tests/MediaApiTests.cs`
- Modify only if the RED test proves a production defect: `src/Cmsify.Api/Controllers/MediaController.cs`
- Modify only if the RED test proves reconciliation ownership is the defect: the smallest relevant file under `src/Cmsify.Infrastructure/BackgroundServices`

**Interfaces:**
- `AvailableAssetWithMissingBlob_ReturnsSanitizedProblemDetails` must always observe an `Available` database row whose provider returns no blob and receive the `media-blob-missing` RFC 7807 response.
- `Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone` must always reach the controller with a live row, return `412 concurrency-mismatch`, retain `IsDeleted == false`, and create no deletion intent.
- Test fixture reconciliation must not mutate state owned by an API assertion unless that test explicitly enables reconciliation.

- [ ] **Step 1: Add a repeatable failing regression**

  Introduce an explicit factory option that controls media reconciliation startup, then add a stress theory or loop (minimum 20 isolated iterations for each retained scenario) that demonstrates the current background-service race before the option is wired. Name the exact production change that would make each test fail in a comment adjacent to the assertion.

- [ ] **Step 2: Run the focused tests and verify RED**

  Run: `dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~AvailableAssetWithMissingBlob|FullyQualifiedName~Delete_WithStaleEtag"`

  Expected: at least one controlled assertion fails because reconciliation can preempt the request, or the new fixture contract is not yet implemented.

- [ ] **Step 3: Implement the smallest isolation/correctness change**

  Disable the reconciliation hosted service only for these controller-contract fixtures using `ConfigureTestServices` service removal/replacement. Do not disable it globally and do not change production semantics unless the deterministic RED evidence shows a production ordering defect independent of test setup.

- [ ] **Step 4: Verify GREEN repeatedly**

  Run the Step 2 command three consecutive times, followed by the complete API integration project.

  Expected: all runs PASS with `media-blob-missing` and `412` outcomes preserved and no tombstone/deletion intent on stale ETag.

- [ ] **Step 5: Commit**

  Commit message: `Make media concurrency contracts deterministic`

---

### Task 3: Preserve rollback failure diagnostics through cleanup

**Files:**
- Modify: `eng/upgrade-tests/rehearsal.mjs`
- Modify: `eng/upgrade-tests/safe-files.mjs`
- Modify: `tests/upgrade/unit/rehearsal.test.mjs`
- Modify: `tests/upgrade/unit/process.test.mjs` or `tests/upgrade/unit/paths.test.mjs` only when the new contract belongs there
- Modify: `docs/operations.md`

**Interfaces:**
- A rollback assertion failure must retain sanitized stage evidence after the cleanup stage runs.
- The final diagnostics directory must contain one bounded JSON report with schema `cmsify.upgrade-diagnostics.v1`, source/candidate identity, failed stage `rollback`, sanitized readiness/assertion evidence, and cleanup outcome.
- Diagnostic serialization must use the existing safe-path and redaction primitives; raw exception objects, environment variables, request headers, connection strings, tokens, and secret values are forbidden.

- [ ] **Step 1: Write the failing rollback-diagnostic test**

  Add a unit test that injects a rollback assertion failure, allows cleanup to complete, and asserts the final report still names `rollback`, contains its safe evidence, records cleanup separately, and contains none of the seeded sentinel secrets.

- [ ] **Step 2: Run the focused test and verify RED**

  Run: `node --test tests/upgrade/unit/rehearsal.test.mjs`

  Expected: FAIL because rollback evidence is currently omitted or overwritten during cleanup/finalization.

- [ ] **Step 3: Implement atomic sanitized report finalization**

  Capture the first failing stage and its `safeEvidence`, append cleanup status without replacing it, write the versioned JSON report through the run-owned diagnostics directory, and bound arrays/text using existing safe-file limits.

- [ ] **Step 4: Verify GREEN and the full upgrade unit suite**

  Run: `node --test tests/upgrade/unit/*.test.mjs`

  Expected: all tests PASS and mutation controls prove rollback evidence cannot be dropped.

- [ ] **Step 5: Commit**

  Commit message: `Retain rollback rehearsal diagnostics`

---

### Task 4: Build a production-like exact-candidate smoke harness

**Files:**
- Create: `eng/release-smoke/cli.mjs`
- Create: `eng/release-smoke/harness.mjs`
- Create: `eng/release-smoke/http.mjs`
- Create: `eng/release-smoke/evidence.mjs`
- Create: `tests/release-smoke/orchestration.test.mjs`
- Create: `tests/release-smoke/evidence.test.mjs`
- Create: `tests/release-smoke/README.md`
- Modify: `.gitignore` only if a run-owned evidence path needs an explicit rule

**Interfaces:**
- CLI: `node eng/release-smoke/cli.mjs certify --api-image <repo:tag> --admin-image <repo:tag> --version <semver> --source-sha <40hex> --output <directory>`.
- The harness consumes images already loaded by the caller and must never build or pull the two candidate images.
- The harness creates run-scoped PostgreSQL 17, MinIO, webhook receiver, OIDC test issuer, API, and Admin resources on one isolated Docker network.
- Scenario order is fixed: descriptor/label identity; PostgreSQL readiness; API live/ready; Admin static assets; local login; workspace API-client auth; representative template/content CRUD with ETag; media upload/download; OIDC API/Admin login/token forwarding; webhook delivery; scheduled publication; graceful API/Admin restart with persistence; matched PostgreSQL plus media backup; destructive canary; restore into fresh run-scoped services; restored-state verification.
- Evidence schema `cmsify.release-smoke.v1` records version, source SHA, candidate image IDs/digests, scenario names/statuses/durations, backup hashes, and sanitized failure summaries. It records no credentials or payload bodies.
- Cleanup is registered immediately after the first resource is created, prints bounded container logs on failure, and removes only names carrying the validated run scope.

- [ ] **Step 1: Write failing orchestration and evidence tests**

  Use injected Docker/process/HTTP adapters to assert exact scenario order, no candidate build/pull command, bounded readiness/retry loops, restart of the same candidate image IDs, backup-before-destructive-canary ordering, restore into fresh volumes, cleanup on every failure point, and redaction of sentinel credentials from JSON evidence.

- [ ] **Step 2: Run the new tests and verify RED**

  Run: `node --test tests/release-smoke/*.test.mjs`

  Expected: FAIL because the CLI, harness, and evidence modules do not exist.

- [ ] **Step 3: Implement minimal testable adapters and orchestration**

  Keep process execution, HTTP requests, evidence shaping, and scenario sequencing in separate modules. Reuse safe subprocess/path patterns from `eng/upgrade-tests` without coupling release smoke to the historical upgrade fixture. Validate all CLI inputs before creating resources.

- [ ] **Step 4: Verify GREEN**

  Run: `node --test tests/release-smoke/*.test.mjs`

  Expected: all tests PASS, including a failure injected at every scenario boundary.

- [ ] **Step 5: Run one local exact-image rehearsal**

  Run the Task 1 locally built images through the CLI with version `0.0.0-local` and the current 40-character source SHA, writing only to `artifacts/release-smoke/local`.

  Expected: every scenario passes or the harness emits a sanitized actionable failure report. Fix any product defect by first adding a focused failing test in its owning project.

- [ ] **Step 6: Commit**

  Commit message: `Certify production-like release candidates`

---

### Task 5: Certify accessibility, clean consumers, SBOMs, attestations, and promoted OCI bytes

**Files:**
- Create: `eng/accessibility/package.json`
- Create: `eng/accessibility/package-lock.json`
- Create: `eng/accessibility/run.mjs`
- Create: `tests/release-contract/accessibility-policy.test.mjs`
- Modify: `.github/workflows/admin-accessibility.yml`
- Modify: `.github/workflows/publish-cmsify.yml`
- Modify: `scripts/release/verify-release-contract.mjs`
- Modify: `scripts/release/verify-release-artifacts.mjs`
- Modify: `tests/release-contract/verify-release-contract.test.mjs`
- Modify: `tests/release-contract/verify-release-artifacts.test.mjs`

**Interfaces:**
- Branch accessibility runs on `workflow_dispatch`, pushes to `main`, and pull requests when Admin source/styles/static assets, the accessibility harness/lock, shared Blazor contracts, or its workflow changes.
- Candidate accessibility downloads and loads the exact Admin OCI archive from the build job, runs axe WCAG 2.0/2.1 A/AA checks against `/login`, and uploads a bounded report.
- Release `artifact-smoke` downloads the single candidate artifact, verifies `SHA256SUMS`, loads both OCI archives, and invokes Task 4 without rebuilding.
- Clean .NET consumer restores only the three candidate `.nupkg` files from a run-owned local source with public sources disabled for those package IDs; clean Node 20/22 consumers install only the downloaded tarball and its declared registry dependencies.
- Candidate certification requires build, artifact smoke, candidate accessibility, both clean-consumer matrices, and upgrade/rollback before attestation.
- The build creates four finalized SPDX documents; candidate checksums bind them. GitHub build provenance attests the checked candidate subjects. Protected promotion installs a SHA-pinned Cosign tool, copies exact OCI layouts, verifies destination digests equal the certified manifest, signs `repository@sha256:digest` keylessly, verifies those signatures, publishes npm with `--provenance`, and never rebuilds.

- [ ] **Step 1: Write failing workflow and artifact mutation tests**

  Assert required accessibility triggers/path filters, locked axe installation, exact candidate download/load, no candidate rebuild, smoke/accessibility dependencies in `certify`, fail-closed steps, `npm publish --provenance`, digest-bound Cosign sign/verify, and checksum/SBOM/provenance subject agreement. Add mutations for a source-built Admin accessibility run, tag-only accessibility, omitted clean package, changed OCI digest, unsigned destination, and a skipped smoke dependency.

- [ ] **Step 2: Run focused tests and verify RED**

  Run: `node --test tests/release-contract/accessibility-policy.test.mjs tests/release-contract/verify-release-contract.test.mjs tests/release-contract/verify-release-artifacts.test.mjs`

  Expected: FAIL on missing triggers, exact-candidate smoke/accessibility, and signing/provenance requirements.

- [ ] **Step 3: Implement locked accessibility and release certification jobs**

  Pin the axe dependency in its lockfile, make `run.mjs` wait with bounded timeouts and emit sanitized JSON/JUnit-compatible evidence, expand branch triggers, and add candidate jobs to the release dependency graph. Preserve one build artifact and protected `release` promotion.

- [ ] **Step 4: Implement promoted-byte signing and provenance checks**

  Add SHA-pinned Cosign installation in `promote`; sign and verify only the digest returned after copying each certified OCI layout. Keep GitHub artifact attestation in `certify`, add npm provenance, and extend artifact verification so manifest/checksum/SBOM identities disagreeing by one byte fail.

- [ ] **Step 5: Verify GREEN**

  Run the Step 2 command, then `node scripts/release/verify-release-contract.mjs`, `npm ci --prefix eng/accessibility`, and the local accessibility runner against a local Admin container.

  Expected: all local checks PASS. Hosted attestation, keyless signing, protected promotion, and registry evidence remain unclaimed until an approved tagged run occurs.

- [ ] **Step 6: Commit**

  Commit message: `Gate release on exact artifact certification`

---

### Task 6: Enforce API compatibility and ship governance/runbooks

**Files:**
- Create: `SECURITY.md`
- Create: `SUPPORT.md`
- Create: `.github/CODEOWNERS`
- Create: `docs/release-runbook.md`
- Create: `docs/rollback-runbook.md`
- Create: `tests/release-contract/governance-policy.test.mjs`
- Modify: `docs/api-compatibility.md`
- Modify: `docs/operations.md`
- Modify: `.github/workflows/openapi-contract.yml`
- Modify: `scripts/release/verify-release-contract.mjs`
- Modify: `tests/release-contract/verify-release-contract.test.mjs`
- Modify: `README.md` and the nearest docs index/navigation file

**Interfaces:**
- Compatibility window: a deprecated stable `/api/v1` operation or field remains available for at least 12 months and through at least one subsequent stable minor release; removal or incompatible semantic change requires `/api/v2` unless the protected emergency gate is approved.
- Deprecations define owner, announcement date, earliest removal date/version, replacement/migration, `Deprecation: true`, and an absolute-date `Sunset` header where HTTP applies.
- OpenAPI comparison remains live-head versus exact base SHA, scoped to `/api/v1`, and uses an immutable digest-pinned oasdiff image. A breaking result requires protected `api-breaking-change-approved` evidence; tool/runtime failure cannot be treated as a breaking result or approval path.
- `SECURITY.md` documents private reporting, supported versions, response targets, disclosure coordination, and forbidden secret/public-issue handling without inventing an unverified address; use the repository security-advisory URL as the primary channel.
- `SUPPORT.md` separates security reports, defects, usage questions, support windows, and end-of-support behavior.
- `CODEOWNERS` assigns release workflow/scripts, public contracts/OpenAPI, security-sensitive auth/webhook/storage, and governance files to a verified GitHub user/team supplied by repository metadata or the authenticated repository configuration. If no valid owner can be verified locally, the file must not invent one; record release ownership in the runbook and keep CODEOWNERS activation as an external governance gate.
- Runbooks define roles, exact commands, evidence, abort criteria, protected approvals, backup/restore, rollback decision points, immutable digests, and the prohibition on rebuild/publish without approval.

- [ ] **Step 1: Write failing policy-presence and semantic tests**

  Assert every file exists and contains the exact compatibility window, private advisory route, ownership surfaces, release preflight/certify/promote split, rollback triggers, backup verification, public-restore gate, and no unsupported claim of hosted protection/publication. Mutate each required clause once and expect a targeted failure.

- [ ] **Step 2: Run the tests and verify RED**

  Run: `node --test tests/release-contract/governance-policy.test.mjs tests/release-contract/verify-release-contract.test.mjs`

  Expected: FAIL because governance files and the enforceable deprecation window are absent.

- [ ] **Step 3: Write the policies and runbooks**

  Use concrete commands already present in workflows/scripts. Mark external configuration such as GitHub environments, registry permissions, and advisory enablement as operator prerequisites requiring verification, not completed facts.

- [ ] **Step 4: Tighten compatibility enforcement and workflow evidence**

  Pin the OpenAPI comparison image, retain exact base/head output, and add checked deprecation inventory/evidence only when an API is actually deprecated. Do not add headers to endpoints with no deprecation decision.

- [ ] **Step 5: Verify GREEN and links**

  Run the Step 2 command, `node scripts/release/verify-release-contract.mjs`, and the documentation link checker already used by the repository if present.

  Expected: all checks PASS and every runbook link resolves locally.

- [ ] **Step 6: Commit**

  Commit message: `Document v1 release governance`

---

### Task 7: Produce checked Task 12 evidence and an honest go/no-go ledger

**Files:**
- Create: `docs/evidence/task-12-local-verification.json`
- Create: `tests/release-contract/task-12-evidence.test.mjs`
- Modify: `docs/v1-release-readiness.md`
- Modify: `docs/v1-release-remediation-handoff.md`
- Modify: `docs/superpowers/plans/2026-08-24-v1-remediation.md`
- Modify: nearest release/operations documentation referenced by the evidence

**Interfaces:**
- Evidence schema: `cmsify.task12-evidence.v1`.
- Required top-level fields: `sourceSha`, `sdkVersion`, `nodeVersion`, `dockerClientVersion`, `dockerServerVersion`, `localFeedPackage`, `checks`, `artifacts`, `externalGates`, and `knownDiagnostics`.
- `localFeedPackage` records ID `SyntaxCircus.Http.Resilience`, version `0.2.0-cmsify.1`, SHA-256 `17843D8C0A3422FCE37A3CEAC38029C638B099F01F044B09F30AD237D1786A1C`, and explicitly says `publicRestoreValidated: false` until the user-owned gate changes.
- Every local check records exact command, exit code, counts where available, and source SHA. Artifact entries may record local content-addressed candidates but must not call them published or promoted.
- External gates are explicit booleans/evidence links for public package restore, hosted accessibility, protected approvals, artifact attestation, registry signing, immutable OCI promotion, production-like hosted smoke/soak, and final release. Unknown/unperformed gates are `false` with a reason, never omitted.

- [ ] **Step 1: Write the failing evidence-contract tests**

  Add schema and semantic tests that reject missing commands/counts, wrong package hash, success claims for public restore without immutable evidence, local images labeled published, checked go/no-go boxes whose external evidence is false, stale source SHA, and documentation contradicting the manifest.

- [ ] **Step 2: Run the focused test and verify RED**

  Run: `node --test tests/release-contract/task-12-evidence.test.mjs`

  Expected: FAIL because the Task 12 evidence manifest does not exist.

- [ ] **Step 3: Run every local certification gate from a clean committed implementation source**

  Run all commands in Task 8 below, capture only sanitized summaries, and bind the manifest to the implementation source SHA that those commands exercised. Do not edit source between the evidence runs and recording their results except for the evidence/docs commit itself.

- [ ] **Step 4: Update the readiness ledger and outer plan**

  Check only outcomes proved by immutable local evidence. Leave user-owned publication, hosted/protected approval, registry signing/promotion, soak, and actual release boxes open. Record owners, reason, and next exact command for every open gate. Mark outer Task 12 repository implementation complete only if its local checks and reviews are clean; do not mark the overall v1 release certified.

- [ ] **Step 5: Verify GREEN**

  Run: `node --test tests/release-contract/task-12-evidence.test.mjs tests/release-contract/quality-documentation.test.mjs`

  Expected: all tests PASS and deliberate public-release overclaims fail.

- [ ] **Step 6: Commit**

  Commit message: `Record Task 12 certification evidence`

---

### Task 8: Run the complete remediation completion gate

**Files:**
- Modify only for test-first fixes exposed by verification: the smallest owning production/test/policy files
- Modify after reruns: `docs/evidence/task-12-local-verification.json`

**Interfaces:**
- The final local tuple is one clean committed source SHA plus exact SDK/Node/Docker versions, package hash, test counts, candidate image IDs/digests, coverage schema, capacity schema, and release-smoke schema.
- A failed gate remains failed until the full covering command is rerun successfully; focused reruns alone do not certify the complete gate.

- [ ] **Step 1: Verify clean exact local restore inputs**

  Run the ignored-feed forced locked restore documented in `docs/performance.md`, then verify all five Cmsify asset graphs resolve `SyntaxCircus.Http.Resilience/0.2.0-cmsify.1` as a package with the exact recorded content hash and no sibling project path.

- [ ] **Step 2: Run warning-free Release build and full .NET suites**

  Run: `dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental`

  Run: `dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1`

  Expected: exit 0, zero first-party warnings/errors, and zero failed tests.

- [ ] **Step 3: Run TypeScript/OpenAPI and clean consumer suites**

  From `sdk/typescript`, run `npm ci`, `npm run generate:check`, `npm run typecheck`, `npm test`, `npm run build`, and `npm run test:consumer` against the locally packed candidate. Run a fresh .NET 10 consumer against all three locally packed candidates.

  Expected: every command exits 0 on the exact candidate files.

- [ ] **Step 4: Run policy, security, upgrade, concurrency, storage, coverage, and capacity gates**

  Run all `tests/release-contract/*.test.mjs`, all `tests/upgrade/unit/*.test.mjs`, deterministic fixture verification, the opt-in Docker upgrade/rollback rehearsal, API and Infrastructure integration projects, coverage summary, and capacity report commands documented by Task 11.

  Expected: all blocking gates pass; diagnostic coverage/latency trends remain labeled diagnostic.

- [ ] **Step 5: Build and certify exact local candidate artifacts**

  Pack the three NuGet packages and npm package with one local prerelease version/source SHA, build both OCI layouts once, finalize four SPDX files, create `SHA256SUMS` and the release manifest, run `verify-release-artifacts.mjs`, clean consumers, candidate accessibility, and the Task 4 production-like smoke against those exact bytes.

  Expected: all local certification passes with immutable local hashes/digests. No public signing/promotion claim is made.

- [ ] **Step 6: Run final branch-wide review**

  Re-read F-01 through F-19 and every go/no-go checkbox against current code and evidence. Obtain a clean review for every task and one clean whole-branch review. If a finding requires a fix, apply TDD, rerun its full covering gate, update evidence, and re-review once.

- [ ] **Step 7: Commit any evidence-only refresh**

  Commit message: `Finalize v1 local certification evidence`
