# Post-merge V1 Release Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resume v1 certification only after the Cmsify remediation and HTTP resilience work are merged, and publish `SyntaxCircus.Http.Resilience` only from its trusted default-branch/release workflow.

**Architecture:** Treat the current branch-built resilience package as local implementation evidence, not a publication candidate. First merge both repositories, build the final resilience package from the sibling repository's default branch, reconcile that exact identity into Cmsify through a follow-up PR if it differs, and only then publish and run Cmsify's public and hosted release gates from updated `main`.

**Tech Stack:** Git/GitHub pull requests, PowerShell 7, .NET SDK 10.0.400, NuGet.org repository signatures, Docker/Buildx/OCI, GitHub Actions, Node.js release verifiers.

**Spec:** `docs/v1-release-remediation-handoff.md`, `docs/release-runbook.md`, and the user's requirement that publication must not originate from a feature branch.

**Completed 2026-09-02:** Every task and completion criterion in this handoff was executed. `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` was published from its protected default-branch workflow, and Cmsify `v0.2.1` was certified and released from exact source `26c064a81411c1ec303fa1dc07813841760d44ea` by workflow [33630027328](https://github.com/Syntax-Circus/cmsify/actions/runs/33630027328). Authenticated soak recorder [33651575272](https://github.com/Syntax-Circus/cmsify/actions/runs/33651575272) completed the final gate. The unchecked steps below preserve the original operator procedure; they are not open work.

## Global Constraints

- Do not publish `SyntaxCircus.Http.Resilience` from `feature/cmsify-resilience` or publish the ignored branch-built `.nupkg` currently used for local evidence.
- Do not publish, tag, sign, attest, promote, or release without separate explicit authorization at the applicable step.
- Use SDK `10.0.400` and preserve locked restore.
- Treat NuGet versions as immutable: never publish `0.2.0-cmsify.1` until the exact default-branch-built bytes and content hash are approved.
- NuGet.org repository signing changes raw archive bytes but preserves the package content hash; verify both the repository signature and preserved content hash.
- Do not reuse the preliminary API-only OCI proof as the definitive release tuple.
- Preserve all completed Tasks 1–12 repository work; do not redo it.

---

### Task 1: Merge the repository work before producing publication bytes

**Files:**
- Verify: `docs/v1-release-remediation-handoff.md`
- Verify: sibling `SyntaxCircus.Http.Resilience` history containing `e5a7c57bbd3f24eb15c66e5d740e05fffd4f1bc3`

**Interfaces:**
- Consumes: the Cmsify PR created from `feature/readiness-audit` and the sibling resilience changes from `feature/cmsify-resilience`.
- Produces: reviewed default-branch commits in both repositories; no package publication.

- [ ] **Step 1: Merge the Cmsify remediation PR through GitHub review**

  Confirm required checks and approvals are green, then merge the PR. Do not create a release tag.

- [ ] **Step 2: Merge the sibling resilience work through its own PR**

  The sibling default branch must contain `e5a7c57bbd3f24eb15c66e5d740e05fffd4f1bc3` in its ancestry. Do not publish from the feature branch.

- [ ] **Step 3: Verify both default branches locally**

  Run in each repository:

  ```powershell
  git switch main
  git pull --ff-only origin main
  git status --short
  git log -5 --oneline
  ```

  Expected: clean worktrees and the reviewed PR commits in `main` history.

### Task 2: Produce and validate the default-branch resilience candidate without publishing

**Files:**
- Produce outside Cmsify: `artifacts/local-nuget/http-resilience/SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg`
- Compare with Cmsify: `docs/evidence/task-12-local-verification.json`

**Interfaces:**
- Consumes: the merged sibling default-branch commit.
- Produces: one unpublished default-branch `.nupkg`, its SHA-256, its NuGet content hash, and its source commit.

- [ ] **Step 1: Build and test the sibling default branch**

  ```powershell
  dotnet build SyntaxCircus.Http.Resilience.slnx --configuration Release -p:DisableGitVersionTask=true --verbosity minimal
  dotnet test tests/SyntaxCircus.Http.Resilience.Tests/SyntaxCircus.Http.Resilience.Tests.csproj --configuration Release --no-restore --verbosity minimal -p:DisableGitVersionTask=true
  ```

  Expected: warning/error policy remains as documented and all sibling tests pass.

- [ ] **Step 2: Pack the exact prerelease from the merged commit**

  Capture the merged source first:

  ```powershell
  $resilienceSourceSha = git rev-parse HEAD
  dotnet pack src/SyntaxCircus.Http.Resilience/SyntaxCircus.Http.Resilience.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -p:Version=0.2.0-cmsify.1 -p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false -p:WarningsAsErrors= -p:RepositoryCommit=$resilienceSourceSha -p:SourceRevisionId=$resilienceSourceSha --output artifacts/local-nuget/http-resilience --verbosity minimal
  ```

  Do not run `dotnet nuget push`.

- [ ] **Step 3: Record the unpublished default-branch identity**

  ```powershell
  $package = 'artifacts/local-nuget/http-resilience/SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg'
  Get-FileHash -LiteralPath $package -Algorithm SHA256
  dotnet nuget verify $package --all --verbosity normal --force-english-output
  ```

  Expected before NuGet.org publication: `NU3004` reports unsigned, while the command prints the candidate content hash. Record the full source SHA, raw SHA-256, content hash, and file size.

- [ ] **Step 4: Compare against the preliminary branch-built identity**

  Compare the new values with `localFeedPackage.localUnsignedSha256` and `localFeedPackage.contentHash` in `docs/evidence/task-12-local-verification.json`. Any difference is expected to require Task 3; it is not permission to reuse or publish the older branch-built package.

### Task 3: Reconcile the default-branch package into Cmsify before publication

**Files:**
- Modify if identity changed: `docs/evidence/task-12-local-verification.json`
- Modify if content hash changed: the five consuming `packages.lock.json` files under `sdk/dotnet`/`src/Cmsify.Admin`/`tests/Cmsify.Admin.Integration.Tests`
- Modify: `docs/v1-release-readiness.md`
- Modify: `docs/v1-release-remediation-handoff.md`
- Test: `tests/release-contract/task-12-evidence.test.mjs`

**Interfaces:**
- Consumes: the unpublished package identity from Task 2.
- Produces: a reviewed Cmsify `main` state whose evidence and locks describe exactly the package that will be published.

- [ ] **Step 1: Create a new post-merge Cmsify branch**

  ```powershell
  git switch main
  git pull --ff-only origin main
  git switch -c release/reconcile-http-resilience-0.2.0-cmsify.1
  ```

- [ ] **Step 2: Replace only the ignored local candidate bytes**

  Copy the Task 2 package into Cmsify's ignored `artifacts/local-nuget/http-resilience` feed. Never commit the package, feed configuration, or package cache.

- [ ] **Step 3: Regenerate the affected locks only when the content hash changed**

  Follow the safe `--force-evaluate` procedure in `docs/performance.md`, using the ignored feed. Confirm exactly five consuming lock entries resolve `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` with the new content hash and no sibling project path.

- [ ] **Step 4: Update preliminary evidence to the default-branch candidate identity**

  Replace the local unsigned SHA-256/content hash/source provenance and keep `publicRestoreValidated`, every definitive candidate gate, every hosted gate, and final release false.

- [ ] **Step 5: Run the complete local validation**

  ```powershell
  dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
  dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental --verbosity minimal
  dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
  ```

  Then run the TypeScript/OpenAPI commands from `AGENTS.md` and the combined release-contract, release-smoke, and upgrade-unit Node gate. Expected: all checks pass, zero .NET skips, and no generated artifacts remain tracked or untracked.

- [ ] **Step 6: Review and merge the reconciliation PR**

  Commit only source/evidence/lock changes, push the new branch, open a PR against `main`, and merge it after checks and review. Do not publish the package before this PR lands.

### Task 4: Publish the approved resilience bytes from the trusted default-branch workflow

**Files:**
- External artifact: `SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg`
- Verify in Cmsify: `docs/evidence/task-12-local-verification.json`

**Interfaces:**
- Consumes: the exact Task 2 package after Task 3 has merged.
- Produces: NuGet.org version `0.2.0-cmsify.1` with a repository signature and the preserved approved content hash.

- [ ] **Step 1: Reconfirm publication authority and immutable identity**

  Require explicit approval naming the exact version, default-branch source SHA, local unsigned SHA-256, content hash, and package path. Confirm NuGet.org does not already contain the version.

- [ ] **Step 2: Publish from the sibling default-branch/release workflow**

  Use the repository's approved protected publishing workflow or default-branch release mechanism. Do not invoke publication from a feature-branch checkout and do not substitute newly packed bytes after approval.

- [ ] **Step 3: Verify NuGet.org repository signing**

  After indexing completes, download the exact flat-container package and run:

  ```powershell
  dotnet nuget verify SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg --all --verbosity normal --force-english-output
  ```

  Expected: exact content hash, `Signature type: Repository`, `Service index: https://api.nuget.org/v3/index.json`, and `Owners: syntaxcircus`. The signed archive's raw SHA-256 may differ from the unsigned candidate and must be recorded separately.

### Task 5: Resume Cmsify certification from updated `main`

**Files:**
- Consume: `scripts/release/verify-task-12-external-gate.ps1`
- Update with final evidence: `docs/evidence/task-12-local-verification.json`
- Update: `docs/v1-release-readiness.md`
- Update: `docs/v1-release-remediation-handoff.md`

**Interfaces:**
- Consumes: the merged Cmsify default branch and publicly repository-signed resilience package.
- Produces: passing public restore evidence and one definitive same-source release candidate tuple.

- [ ] **Step 1: Start from current default branch, never the old feature branch**

  ```powershell
  git switch main
  git pull --ff-only origin main
  git status --short
  ```

- [ ] **Step 2: Run the isolated public-package gate**

  ```powershell
  pwsh -NoProfile -NonInteractive -File scripts/release/verify-task-12-external-gate.ps1 -Gate public-package-restore
  ```

  Expected: official download, repository-signature/content-hash verification, five exact locks, clean public-only locked restore, five exact asset graphs, and matching downloaded/cached signed archive SHA-256.

- [ ] **Step 3: Repeat the complete public build/test/SDK gate**

  Use only public NuGet sources. Run the full .NET, TypeScript/OpenAPI, Node release-policy, clean-consumer, and standalone verifier commands documented in `AGENTS.md` and `docs/release-runbook.md`.

- [ ] **Step 4: Build one definitive same-source candidate tuple**

  Build the NuGet/npm/API/Admin candidates once from the exact reviewed Cmsify `main` source. Record package hashes, OCI manifest digests, Docker image IDs/config digests, source labels, platform, SBOM subjects, and complete `SHA256SUMS`. Do not reuse the preliminary API-only archive.

### Task 6: Execute the protected release sequence

**Files:**
- Follow: `docs/release-runbook.md`
- Follow: `docs/rollback-runbook.md`
- Verify: `.github/workflows/publish-cmsify.yml`

**Interfaces:**
- Consumes: Task 5's exact candidate tuple.
- Produces: hosted certification and, only after explicit approval, the v1 release.

- [ ] **Step 1: Run all candidate certification jobs**

  Complete clean consumers, candidate accessibility, exact-image upgrade/rollback, production-like smoke, matched backup/restart/restore, and hosted accessibility against the same source and artifacts.

- [ ] **Step 2: Complete protected approvals and artifact authentication**

  Verify complete subject attestations, registry signatures, immutable digest-preserving promotion, and authenticated soak evidence. Any mismatch aborts the release.

- [ ] **Step 3: Create and push the stable tag only after authorization**

  Use the tag-push-only workflow from the reviewed default branch. Do not rebuild or substitute artifacts after certification.

- [ ] **Step 4: Run the final release gate and close the ledger**

  Require every machine-readable external/final gate to be true with immutable evidence links before marking v1 ready. Update the handoff/readiness documents, obtain final review, and preserve the rollback diagnostics and historical media-race adjudication.

## Completion Criteria

- Both feature branches are merged before any package publication.
- The published resilience package was built and approved from the sibling default branch, not from a feature branch.
- Cmsify `main` evidence and locks match the exact package published to NuGet.org.
- Public restore, definitive candidate, hosted certification, approvals, signing/attestation, immutable promotion, soak, tag, and final release gates all pass against one source/artifact tuple.
- No ignored feed, package bytes, cache, secrets, generated output, or local evidence artifacts are committed.
