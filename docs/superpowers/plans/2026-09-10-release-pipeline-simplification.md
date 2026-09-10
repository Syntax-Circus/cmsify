# Release Pipeline Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Cmsify's ~17,000-line release pipeline (offline OCI-loader transport, a 1,100-line self-testing governance verifier, a moving-baseline upgrade/rollback rehearsal, soak recording) with a ~250-line, 3-job `publish-cmsify.yml` that keeps only Cosign signing, SBOM generation, npm/NuGet trusted publishing, and a manual approval gate before anything publishes.

**Architecture:** `resolve` (validate tag) → `build-test-and-package` (build/test/pack, build both Docker images locally, smoke-test them together against a real Postgres via plain `docker run`/`curl`, save the smoke-tested images) → `promote` (`environment: release`, human-gated — push images, sign, generate SBOMs, publish NuGet/npm, create the GitHub Release). Nothing touches a registry, and no signature or package is published, until a human approves the `release` environment. Everything that isn't in this 3-job chain — the offline Skopeo/OCI-loader, the upgrade/rollback rehearsal, soak recording, and the governance verifier — is deleted outright, not migrated.

**Tech Stack:** GitHub Actions, Docker Buildx, Cosign, Syft, dotnet CLI, npm CLI, gh CLI.

**Spec:** `docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md`

## Global Constraints

- Branch: `simplify/release-pipeline` (already created off `main`).
- Every `uses:` action reference in any workflow YAML must be pinned to a 40-character commit SHA with a `# vX.Y.Z` comment, matching the existing style in `publish-cmsify.yml` — this repo's convention, not a rule from the deleted governance verifier.
- Docker Hub credentials are already available as `secrets.DOCKERHUB_USERNAME` / `secrets.DOCKERHUB_TOKEN` (used by the current `promote` job) — no new secrets needed anywhere in this plan.
- The `environment: release` gate (manual approval) must be the first point at which anything is pushed to a registry, signed, published, or released. Nothing upstream of that gate may push, sign, publish, or create a release — this is the one property from the old design that must not regress.
- Do not touch: `.github/workflows/admin-accessibility.yml`, `openapi-contract.yml`, `dotnet-test.yml`'s other jobs (only its `release-contract` job is deleted — see Task 5), `capacity-trends.yml`, `typescript-sdk.yml`, `docker-compose.yml`, `docker-compose.prod.yml`, any application source code.
- Commit after every task.

---

### Task 1: Delete the offline OCI loader and the old smoke-test harness

**Files:**
- Delete: `scripts/release/load-oci-candidate.mjs`
- Delete: `scripts/release/finalize-spdx.mjs`
- Delete: `scripts/release/verify-release-artifacts.mjs`
- Delete: `scripts/release/verify-task-12-external-gate.ps1`
- Delete: `tests/release-contract/load-oci-candidate.test.mjs`
- Delete directory: `eng/release-smoke/`
- Delete directory: `tests/release-smoke/`
- Delete: `.github/workflows/mirror-skopeo.yml`
- Delete: `docs/evidence/task-12-local-verification.json` (delete the `docs/evidence/` directory too if this was its only file)

**Interfaces:** None — these files have no consumers left once Task 4 (governance verifier) and Task 6 (new `publish-cmsify.yml`) are also done, but deleting them now is safe on its own: nothing outside `publish-cmsify.yml`, `scripts/release/verify-release-contract.mjs`, `scripts/release/governance-validator.mjs`, and `tests/release-contract/` references any of them (confirmed by repo-wide grep during planning).

- [ ] **Step 1: Delete the files and directories**

```bash
git rm scripts/release/load-oci-candidate.mjs scripts/release/finalize-spdx.mjs scripts/release/verify-release-artifacts.mjs scripts/release/verify-task-12-external-gate.ps1
git rm tests/release-contract/load-oci-candidate.test.mjs
git rm -r eng/release-smoke tests/release-smoke
git rm .github/workflows/mirror-skopeo.yml
git rm -r docs/evidence
```

- [ ] **Step 2: Confirm nothing outside `publish-cmsify.yml` / the governance verifier still references them**

```bash
grep -rln "load-oci-candidate\|finalize-spdx\|verify-release-artifacts\|verify-task-12-external-gate\|eng/release-smoke\|tests/release-smoke\|mirror-skopeo\|task-12-local-verification" --include="*.yml" --include="*.mjs" --include="*.md" . 2>/dev/null
```

Expected: only `.github/workflows/publish-cmsify.yml`, `scripts/release/verify-release-contract.mjs`, `tests/release-contract/*`, `docs/release-runbook.md`, and historical files under `docs/superpowers/`/`.superpowers/sdd/` — all of which are handled by Tasks 4, 6, and 7. If anything else shows up, stop and investigate before continuing.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore(release): delete the offline OCI loader and old smoke-test harness

Both are being replaced by a plain docker build/run/curl smoke test
in the simplified publish-cmsify.yml (see docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md)."
```

---

### Task 2: Delete the upgrade/rollback rehearsal

**Files:**
- Delete: `.github/workflows/upgrade-rollback.yml`
- Delete directory: `eng/upgrade-tests/`
- Delete directory: `tests/upgrade/`

**Interfaces:** None. `docs/operations.md` references `tests/upgrade/` and `eng/upgrade-tests/` — fixed in Task 7, not here (keep this task focused on deletion only).

- [ ] **Step 1: Delete**

```bash
git rm .github/workflows/upgrade-rollback.yml
git rm -r eng/upgrade-tests tests/upgrade
```

- [ ] **Step 2: Confirm no other live workflow references the deleted paths**

```bash
grep -rln "eng/upgrade-tests\|tests/upgrade" --include="*.yml" .github/workflows/ 2>/dev/null
```

Expected: no output (only `publish-cmsify.yml` referenced `eng/upgrade-tests`/`tests/upgrade`, and it's rewritten in Task 6).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore(release): delete the upgrade/rollback rehearsal

Hand-duplicated a large slice of the Content API's JSON contract as
static assertions that repeatedly went stale against real API changes
(most recently the content-version-unification breaking change), and
there is no real production install of Cmsify yet to protect. See
docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 3: Delete soak recording

**Files:**
- Delete: `.github/workflows/record-release-soak.yml`

**Interfaces:** None.

- [ ] **Step 1: Delete**

```bash
git rm .github/workflows/record-release-soak.yml
```

- [ ] **Step 2: Confirm nothing else references it**

```bash
grep -rln "record-release-soak\|cmsify-hosted-soak" --include="*.yml" --include="*.mjs" . 2>/dev/null
```

Expected: only `docs/release-runbook.md` (fixed in Task 7) and, if present, `tests/release-contract/task-12-external-gates.test.mjs` (already deleted in Task 4, whichever order these run in — if Task 4 hasn't run yet, that file appearing here is expected and fine).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore(release): delete soak recording

Recorded a 60-minute post-release soak for the upgrade-rollback job,
which no longer exists. See
docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 4: Delete the governance verifier and its test suite

**Files:**
- Delete: `scripts/release/verify-release-contract.mjs`
- Delete: `scripts/release/governance-validator.mjs`
- Delete directory: `tests/release-contract/`

**Interfaces:** None outside `dotnet-test.yml`'s `release-contract` job, which is deleted in Task 5.

- [ ] **Step 1: Delete**

```bash
git rm scripts/release/verify-release-contract.mjs scripts/release/governance-validator.mjs
git rm -r tests/release-contract
```

- [ ] **Step 2: Confirm only `dotnet-test.yml` and docs still reference these**

```bash
grep -rln "verify-release-contract\|governance-validator\|tests/release-contract" --include="*.yml" --include="*.mjs" --include="*.json" . 2>/dev/null
```

Expected: `.github/workflows/dotnet-test.yml` (Task 5), `docs/performance.md` (Task 7), and historical `docs/superpowers/`/`.superpowers/sdd/` files (leave alone).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore(release): delete the governance verifier and its test suite

1,100+ lines that re-asserted the exact wording of the release
workflow's YAML rather than verifying real release behavior — this is
what let the upgrade-rollback continue-on-error regression silently
break the entire suite on every PR. See
docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 5: Remove the dead `release-contract` job from `dotnet-test.yml`

The governance verifier and upgrade-test fixture this job exercises are both deleted (Tasks 2 and 4); without this change, every future PR's CI run fails immediately with "file not found."

**Files:**
- Modify: `.github/workflows/dotnet-test.yml`

**Interfaces:** None — this job has no outputs consumed elsewhere in `dotnet-test.yml` (confirmed during planning: it's a standalone job with no `needs:` and nothing needs it).

- [ ] **Step 1: Read the job and its surrounding structure**

```bash
grep -n "^  [a-z-]*:$" .github/workflows/dotnet-test.yml
```

Confirm `release-contract:` is a top-level job key (it was, at the point this plan was written — starting at the line containing `release-contract:` and ending at the file's last line, since it's the final job in the file).

- [ ] **Step 2: Delete the job**

Remove the entire `release-contract:` job block — from the line `  release-contract:` through the end of the file (it is the last job in `dotnet-test.yml`), including the blank line immediately before it that separates it from the preceding job.

- [ ] **Step 3: Verify the workflow still parses and no other job references `release-contract`**

```bash
grep -n "needs:.*release-contract\|release-contract" .github/workflows/dotnet-test.yml
docker run --rm -v "$PWD":/repo --workdir /repo rhysd/actionlint:latest -color .github/workflows/dotnet-test.yml
```

Expected: first command produces no output; second command reports no errors.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/dotnet-test.yml
git commit -m "chore(ci): remove dotnet-test.yml's release-contract job

Its targets (the governance verifier and the upgrade-rollback fixture)
are both deleted. See
docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 6: Rewrite `publish-cmsify.yml` as the 3-job pipeline

**Files:**
- Modify: `.github/workflows/publish-cmsify.yml` (full rewrite)

**Interfaces:**
- Produces: job outputs `resolve.outputs.version`, `resolve.outputs.source_sha`, `resolve.outputs.is_prerelease`, `resolve.outputs.npm_channel` (unchanged names/shapes from the current workflow); `promote`'s `push` step outputs `api_digest`, `admin_digest` (new, consumed only within the same job).
- Consumes: `scripts/release/validate-release-tag.mjs` (unchanged, kept as-is), `src/Cmsify.Api/Dockerfile` and `src/Cmsify.Admin/Dockerfile` build args `BUILD_VERSION`, `BUILD_INFORMATIONAL_VERSION`, `BUILD_SOURCE_REVISION` (unchanged, same names the current workflow already passes), `secrets.DOCKERHUB_USERNAME`, `secrets.DOCKERHUB_TOKEN`, `secrets.NUGET_USER`, `github.token` (all already configured).

- [ ] **Step 1: Replace the entire file**

Write this exact content to `.github/workflows/publish-cmsify.yml`, replacing everything currently there:

```yaml
name: Certify and promote Cmsify release

on:
  push:
    tags: ["v*"]

permissions:
  contents: read

concurrency:
  group: cmsify-release-${{ github.ref }}
  cancel-in-progress: false

jobs:
  resolve:
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.release.outputs.version }}
      source_sha: ${{ steps.release.outputs.source_sha }}
      is_prerelease: ${{ steps.release.outputs.is_prerelease }}
      npm_channel: ${{ steps.release.outputs.npm_channel }}
    steps:
      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
        with: { fetch-depth: 0, persist-credentials: false }
      - id: release
        shell: bash
        run: |
          SOURCE_SHA="$(git rev-list -n 1 "$GITHUB_REF")"
          VERSION="$(node scripts/release/validate-release-tag.mjs "$GITHUB_REF_NAME" --source-sha "$SOURCE_SHA" --require-changelog)"
          if [[ "$VERSION" == *-* ]]; then IS_PRERELEASE=true; NPM_CHANNEL=next; else IS_PRERELEASE=false; NPM_CHANNEL=latest; fi
          printf 'version=%s\nsource_sha=%s\nis_prerelease=%s\nnpm_channel=%s\n' "$VERSION" "$SOURCE_SHA" "$IS_PRERELEASE" "$NPM_CHANNEL" >> "$GITHUB_OUTPUT"

  build-test-and-package:
    needs: resolve
    runs-on: ubuntu-latest
    permissions: { contents: read }
    env:
      VERSION: ${{ needs.resolve.outputs.version }}
      SOURCE_SHA: ${{ needs.resolve.outputs.source_sha }}
    steps:
      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
        with:
          ref: ${{ needs.resolve.outputs.source_sha }}
          fetch-depth: 1
          persist-credentials: false
      - uses: actions/setup-dotnet@d4c94342e560b34958eacfc5d055d21461ed1c5d # v5.0.0
        with: { global-json-file: global.json }
      - uses: actions/setup-node@a0853c24544627f65ddf259abe73b1d18a591444 # v5.0.0
        with: { node-version: "22", cache: npm, cache-dependency-path: sdk/typescript/package-lock.json }
      - uses: docker/setup-buildx-action@e468171a9de216ec08956ac3ada2f0791b6bd435 # v3.11.1
      - name: Build, test, and pack the .NET packages
        run: |
          dotnet restore Cmsify.slnx --locked-mode
          dotnet build Cmsify.slnx --configuration Release --no-restore -p:Version="$VERSION" -p:InformationalVersion="$VERSION+$SOURCE_SHA"
          dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
          mkdir -p artifacts/nuget
          dotnet pack src/Cmsify.Contracts/Cmsify.Contracts.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false
          dotnet pack sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
          dotnet pack sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
          dotnet pack src/Cmsify.Components/Cmsify.Components.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
          dotnet pack src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj --configuration Release --no-build --output artifacts/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="$VERSION" -p:RepositoryCommit="$SOURCE_SHA" -p:IncludeSymbols=false
      - name: Pack the npm package
        working-directory: sdk/typescript
        run: |
          npm ci
          npm pkg delete private
          npm pkg set version="$VERSION" gitHead="$SOURCE_SHA"
          npm run generate:check && npm run typecheck && npm test && npm run build
          mkdir -p ../../artifacts/npm && npm pack --pack-destination ../../artifacts/npm
      - name: Build both Docker images locally
        run: |
          docker buildx build --platform linux/amd64 --load --tag "docker.io/syntaxcircus/cmsify-api:$VERSION" --build-arg "BUILD_VERSION=$VERSION" --build-arg "BUILD_INFORMATIONAL_VERSION=$VERSION+$SOURCE_SHA" --build-arg "BUILD_SOURCE_REVISION=$SOURCE_SHA" --file src/Cmsify.Api/Dockerfile .
          docker buildx build --platform linux/amd64 --load --tag "docker.io/syntaxcircus/cmsify-admin:$VERSION" --build-arg "BUILD_VERSION=$VERSION" --build-arg "BUILD_INFORMATIONAL_VERSION=$VERSION+$SOURCE_SHA" --build-arg "BUILD_SOURCE_REVISION=$SOURCE_SHA" --file src/Cmsify.Admin/Dockerfile .
      - name: Smoke test both images together
        run: |
          docker network create cmsify-smoke
          docker run -d --network cmsify-smoke --name cmsify-smoke-postgres -e POSTGRES_DB=cmsify -e POSTGRES_USER=cmsify -e POSTGRES_PASSWORD=cmsify-smoke postgres:17
          for i in $(seq 1 30); do docker exec cmsify-smoke-postgres pg_isready -U cmsify -d cmsify > /dev/null 2>&1 && break; sleep 2; done
          docker run -d --network cmsify-smoke --name cmsify-smoke-api -p 18080:8080 \
            -e ASPNETCORE_URLS=http://+:8080 \
            -e ConnectionStrings__Cmsify="Host=cmsify-smoke-postgres;Port=5432;Database=cmsify;Username=cmsify;Password=cmsify-smoke" \
            -e Cors__AllowedOrigins__0=http://localhost \
            -e Seed__Admin__Email=smoke@example.com \
            -e Seed__Admin__DisplayName="Smoke Admin" \
            -e Seed__Admin__Password="Cmsify-smoke-test-only-1!" \
            "docker.io/syntaxcircus/cmsify-api:$VERSION"
          curl --fail --retry 20 --retry-delay 3 --retry-connrefused http://127.0.0.1:18080/health/ready
          docker run -d --network cmsify-smoke --name cmsify-smoke-admin -p 18081:8080 \
            -e ASPNETCORE_URLS=http://+:8080 \
            -e Admin__ApiBaseUrl=http://cmsify-smoke-api:8080 \
            "docker.io/syntaxcircus/cmsify-admin:$VERSION"
          curl --fail --retry 20 --retry-delay 3 --retry-connrefused http://127.0.0.1:18081/login
      - name: Tear down smoke containers
        if: always()
        run: |
          docker rm -f cmsify-smoke-postgres cmsify-smoke-api cmsify-smoke-admin 2>/dev/null || true
          docker network rm cmsify-smoke 2>/dev/null || true
      - name: Save both smoke-tested images for the gated promote job
        run: |
          mkdir -p artifacts/images
          docker save "docker.io/syntaxcircus/cmsify-api:$VERSION" | gzip > artifacts/images/cmsify-api.tar.gz
          docker save "docker.io/syntaxcircus/cmsify-admin:$VERSION" | gzip > artifacts/images/cmsify-admin.tar.gz
      - uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2
        with:
          name: release-candidate-${{ needs.resolve.outputs.version }}-${{ needs.resolve.outputs.source_sha }}
          path: artifacts
          if-no-files-found: error
          retention-days: 14

  promote:
    needs: [resolve, build-test-and-package]
    runs-on: ubuntu-latest
    environment: release
    permissions: { contents: write, id-token: write }
    env:
      VERSION: ${{ needs.resolve.outputs.version }}
      SOURCE_SHA: ${{ needs.resolve.outputs.source_sha }}
      NPM_CHANNEL: ${{ needs.resolve.outputs.npm_channel }}
      IS_PRERELEASE: ${{ needs.resolve.outputs.is_prerelease }}
    steps:
      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
        with:
          ref: ${{ needs.resolve.outputs.source_sha }}
          fetch-depth: 1
          persist-credentials: false
      - uses: actions/setup-dotnet@d4c94342e560b34958eacfc5d055d21461ed1c5d # v5.0.0
        with: { global-json-file: global.json }
      - uses: actions/setup-node@a0853c24544627f65ddf259abe73b1d18a591444 # v5.0.0
        with: { node-version: "22" }
      - uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2
        with: { cosign-release: v3.0.6 }
      - id: syft
        uses: anchore/sbom-action/download-syft@e22c389904149dbc22b58101806040fa8d37a610 # v0.24.0
        with: { syft-version: v1.42.3 }
      - uses: actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4.3.0
        with:
          name: release-candidate-${{ needs.resolve.outputs.version }}-${{ needs.resolve.outputs.source_sha }}
          path: artifacts
      - name: Reject existing NuGet versions before promotion
        shell: bash
        run: |
          NUGET_VERSION="${VERSION,,}"
          for package in syntaxcircus.cmsify.contracts syntaxcircus.cmsify.client syntaxcircus.cmsify.client.distributedcaching syntaxcircus.cmsify.components syntaxcircus.cmsify.components.theme; do
            http_code="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --connect-timeout 20 --max-time 60 "https://api.nuget.org/v3-flatcontainer/$package/$NUGET_VERSION/$package.$NUGET_VERSION.nupkg")" || exit 1
            case "$http_code" in 404) ;; 200) exit 1 ;; *) echo "Unexpected NuGet preflight HTTP $http_code"; exit 1 ;; esac
          done
      - name: Reject an existing npm version before promotion
        shell: bash
        run: |
          npm_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --connect-timeout 20 --max-time 60 "https://registry.npmjs.org/@syntaxcircus%2Fcmsify-client/$VERSION")" || exit 1
          case "$npm_status" in 404) ;; *) echo "npm version must be absent; registry returned HTTP $npm_status"; exit 1 ;; esac
      - name: Load the smoke-tested images
        run: |
          gunzip -c artifacts/images/cmsify-api.tar.gz | docker load
          gunzip -c artifacts/images/cmsify-admin.tar.gz | docker load
      - name: Log in to Docker Hub
        run: echo "${{ secrets.DOCKERHUB_TOKEN }}" | docker login docker.io --username "${{ secrets.DOCKERHUB_USERNAME }}" --password-stdin
      - id: push
        name: Push both images and capture their digests
        shell: bash
        run: |
          docker push "docker.io/syntaxcircus/cmsify-api:$VERSION"
          docker push "docker.io/syntaxcircus/cmsify-admin:$VERSION"
          if [ "$IS_PRERELEASE" != true ]; then
            docker tag "docker.io/syntaxcircus/cmsify-api:$VERSION" docker.io/syntaxcircus/cmsify-api:latest
            docker tag "docker.io/syntaxcircus/cmsify-admin:$VERSION" docker.io/syntaxcircus/cmsify-admin:latest
            docker push docker.io/syntaxcircus/cmsify-api:latest
            docker push docker.io/syntaxcircus/cmsify-admin:latest
          fi
          API_DIGEST="$(docker image inspect --format '{{index .RepoDigests 0}}' "docker.io/syntaxcircus/cmsify-api:$VERSION" | cut -d@ -f2)"
          ADMIN_DIGEST="$(docker image inspect --format '{{index .RepoDigests 0}}' "docker.io/syntaxcircus/cmsify-admin:$VERSION" | cut -d@ -f2)"
          printf 'api_digest=%s\nadmin_digest=%s\n' "$API_DIGEST" "$ADMIN_DIGEST" >> "$GITHUB_OUTPUT"
      - name: Sign and verify both images by digest
        env: { COSIGN_YES: "true" }
        run: |
          API_SUBJECT="docker.io/syntaxcircus/cmsify-api@${{ steps.push.outputs.api_digest }}"
          ADMIN_SUBJECT="docker.io/syntaxcircus/cmsify-admin@${{ steps.push.outputs.admin_digest }}"
          cosign sign --yes "$API_SUBJECT"
          cosign sign --yes "$ADMIN_SUBJECT"
          cosign verify --certificate-identity "https://github.com/$GITHUB_WORKFLOW_REF" --certificate-oidc-issuer https://token.actions.githubusercontent.com "$API_SUBJECT"
          cosign verify --certificate-identity "https://github.com/$GITHUB_WORKFLOW_REF" --certificate-oidc-issuer https://token.actions.githubusercontent.com "$ADMIN_SUBJECT"
      - name: Generate an SBOM for each image
        run: |
          mkdir -p artifacts/sbom
          ${{ steps.syft.outputs.cmd }} "registry:docker.io/syntaxcircus/cmsify-api@${{ steps.push.outputs.api_digest }}" -o spdx-json=artifacts/sbom/cmsify-api.spdx.json
          ${{ steps.syft.outputs.cmd }} "registry:docker.io/syntaxcircus/cmsify-admin@${{ steps.push.outputs.admin_digest }}" -o spdx-json=artifacts/sbom/cmsify-admin.spdx.json
      - id: nuget_login
        uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0
        with:
          user: ${{ secrets.NUGET_USER }}
      - run: dotnet nuget push artifacts/nuget/*.nupkg --api-key "${{ steps.nuget_login.outputs.NUGET_API_KEY }}" --source https://api.nuget.org/v3/index.json
      - name: Use supported npm OIDC trusted publishing
        run: npm install --global npm@11.11.0 && test "$(npm --version)" = 11.11.0
      - run: npm publish artifacts/npm/*.tgz --access public --provenance --tag "$NPM_CHANNEL"
      - name: Create the GitHub Release
        env:
          GH_TOKEN: ${{ github.token }}
        shell: bash
        run: |
          args=(); if [ "$IS_PRERELEASE" = true ]; then args+=(--prerelease); fi
          gh release create "v$VERSION" artifacts/sbom/*.spdx.json --title "Cmsify v$VERSION" --generate-notes --target "$SOURCE_SHA" "${args[@]}"
```

- [ ] **Step 2: Lint the new workflow**

```bash
docker run --rm -v "$PWD":/repo --workdir /repo rhysd/actionlint:latest -color .github/workflows/publish-cmsify.yml
```

Expected: no errors. If `actionlint` flags an expression or shell issue, fix it before continuing — do not proceed with a workflow actionlint has flagged.

- [ ] **Step 3: Confirm the job count and gate placement**

```bash
grep -c "^  [a-z-]*:$" .github/workflows/publish-cmsify.yml
grep -n "environment: release" .github/workflows/publish-cmsify.yml
```

Expected: 3 jobs (`resolve`, `build-test-and-package`, `promote`); `environment: release` appears exactly once, on `promote`, and every `docker push`, `cosign sign`, `dotnet nuget push`, `npm publish`, and `gh release create` in the file is a step inside that same `promote` job (re-read the file if unsure — this is the property that must hold).

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/publish-cmsify.yml
git commit -m "feat(release): rewrite publish-cmsify.yml as a 3-job pipeline

resolve -> build-test-and-package -> promote (environment: release).
Drops the offline OCI-loader transport in favor of a plain local
docker build, a real docker-run/curl smoke test against Postgres, and
docker save/load to carry the smoke-tested images into the gated
promote job. Keeps Cosign signing, SBOM generation, npm/NuGet trusted
publishing, and the manual approval gate unchanged in substance. See
docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 7: Rewrite the release docs and fix dangling references

**Files:**
- Modify: `docs/release-runbook.md` (full rewrite)
- Modify: `docs/rollback-runbook.md` (full rewrite)
- Modify: `docs/operations.md` (rewrite the "v0.1.x to v1 upgrade and rollback" subsection under "Upgrades and rollback")
- Modify: `docs/performance.md` (remove three now-dead verification command lines)

**Interfaces:** None — documentation only.

- [ ] **Step 1: Rewrite `docs/release-runbook.md`**

Replace the entire file with:

```markdown
# Release runbook

This runbook describes how a maintainer ships a Cmsify release by pushing a validated SemVer tag, which triggers `publish-cmsify.yml`.

## Preflight

Before tagging, from the exact commit you intend to release:

```powershell
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental --verbosity minimal
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
```

Add a dated `## [X.Y.Z] - YYYY-MM-DD` section to `CHANGELOG.md` — `scripts/release/validate-release-tag.mjs` refuses the tag without one (an `## [Unreleased]` placeholder does not count).

## Tag and let the pipeline run

```powershell
git tag vX.Y.Z <commit>
git push origin vX.Y.Z
```

`publish-cmsify.yml` runs three jobs:

1. **`resolve`** — validates the tag and changelog entry, resolves the version and source SHA.
2. **`build-test-and-package`** — builds and tests the .NET solution, packs the five NuGet packages and the npm SDK package, builds both Docker images locally, and smoke-tests them together against a real PostgreSQL container (`docker run` + `curl` against `/health/ready` for the API, `/login` for Admin). Nothing is pushed, signed, or published in this job — if the smoke test fails, nothing downstream runs and no image has touched a registry.
3. **`promote`** (`environment: release`, requires manual approval) — pushes both smoke-tested images to Docker Hub (plus `:latest` for a stable release), signs them with Cosign, generates an SBOM for each with Syft, publishes the NuGet packages and npm package via trusted OIDC publishing, and creates the GitHub Release with the SBOMs attached.

Approve the `release` environment deployment in the Actions run when you're satisfied the smoke test passed and the build looks right. Nothing publishes anywhere until that approval.

## If a release is only partially published

If `promote` fails partway through (for example, Docker images pushed but NuGet push failed), do not retry the same tag — some registries were already written to under that immutable version. Record what succeeded, then cut the next patch version as a new, complete release. Do not reuse or repair a partially-published version tag.

Use the [rollback runbook](rollback-runbook.md) if a deployed version needs to be rolled back after release.
```

- [ ] **Step 2: Rewrite `docs/rollback-runbook.md`**

First, read the current file to confirm it doesn't contain any deployment-specific detail worth preserving beyond what's captured here:

```bash
cat docs/rollback-runbook.md
```

Then replace the entire file with:

```markdown
# Rollback runbook

Use this when a deployed Cmsify version needs to be rolled back.

## Rolling back a deployment

1. Identify the last known-good version tag (the one running before the problematic deploy).
2. Update `CMSIFY_VERSION` in your `.env`/`.env.prod` to that prior version.
3. Pull and restart: `docker compose --env-file .env.prod -f docker-compose.prod.yml pull && docker compose --env-file .env.prod -f docker-compose.prod.yml up -d`.
4. Verify `/health/live`, `/health/ready`, Admin sign-in, and a representative content read before considering the rollback complete.

## Data considerations

Rolling back the application version does not roll back the database. If the problematic release included a breaking database migration, rolling back the image alone is not sufficient — restore the matched database (and media, if applicable) backup taken before that migration ran. Never restore only the database or only media independently; a mismatch between them can leave content pointing at missing or incorrect files.

If no backup was taken before the problematic release, do not attempt to reverse a data migration by hand. Investigate the specific migration's `Down()` method (if one exists) and treat this as an incident, not a routine rollback.
```

- [ ] **Step 3: Rewrite the "v0.1.x to v1 upgrade and rollback" subsection in `docs/operations.md`**

Find the subsection starting at `### v0.1.x to v1 upgrade and rollback` (currently describes the deleted upgrade-rollback rehearsal fixture and its eleven-phase certification) and replace it, from that heading through the paragraph that ends `...before deploying a new stable release.` (read the file to find the exact end boundary — it is the paragraph immediately before the next `###` heading), with:

```markdown
### Upgrading across major versions

There is no longer an automated upgrade rehearsal harness. Before upgrading across a version with breaking changes noted in `CHANGELOG.md` (search for "Breaking" under the target version's section), read those breaking-change notes carefully, take a full backup per the checklist above, and validate the new version against a copy of production data in a non-production environment first if the breaking change touches data migration.
```

- [ ] **Step 4: Remove the three dead verification lines from `docs/performance.md`**

Remove these three lines (each appears once, in different sections — remove each individually, keeping the rest of its surrounding fenced code block intact):

```
node --test tests/release-contract/quality-policy.test.mjs
```

```
node --test tests/release-contract/coverage-summary.test.mjs
```

```
node --test tests/release-contract/capacity-report.test.mjs
```

- [ ] **Step 5: Confirm no remaining references to deleted paths in live docs**

```bash
grep -rln "eng/upgrade-tests\|tests/upgrade\|tests/release-contract\|load-oci-candidate\|governance-validator\|verify-release-contract\|mirror-skopeo\|record-release-soak\|eng/release-smoke\|tests/release-smoke\|verify-release-artifacts\|verify-task-12-external-gate\|finalize-spdx\|task-12-local-verification" docs/*.md
```

Expected: no output. (Historical docs under `docs/superpowers/` and `.superpowers/sdd/` are intentionally excluded from this check — they're dated records, not live documentation.)

- [ ] **Step 6: Commit**

```bash
git add docs/release-runbook.md docs/rollback-runbook.md docs/operations.md docs/performance.md
git commit -m "docs(release): rewrite runbooks and fix references to deleted release machinery

See docs/superpowers/specs/2026-09-10-release-pipeline-simplification-design.md."
```

---

### Task 8: Final verification pass

**Files:** None modified — this task only runs commands.

**Interfaces:** None.

- [ ] **Step 1: Lint every workflow file in the repo**

```bash
docker run --rm -v "$PWD":/repo --workdir /repo rhysd/actionlint:latest -color
```

Expected: no errors across any `.github/workflows/*.yml` file.

- [ ] **Step 2: Confirm the deleted directories are actually gone and nothing references them**

```bash
for path in scripts/release/load-oci-candidate.mjs scripts/release/finalize-spdx.mjs scripts/release/verify-release-artifacts.mjs scripts/release/verify-task-12-external-gate.ps1 scripts/release/verify-release-contract.mjs scripts/release/governance-validator.mjs eng/release-smoke tests/release-smoke eng/upgrade-tests tests/upgrade tests/release-contract .github/workflows/mirror-skopeo.yml .github/workflows/upgrade-rollback.yml .github/workflows/record-release-soak.yml docs/evidence; do
  test -e "$path" && echo "STILL EXISTS: $path"
done
echo "done"
```

Expected: only `done` printed — no `STILL EXISTS` lines.

- [ ] **Step 3: Dry-run the buildable pieces locally, exactly as the workflow will run them**

```bash
dotnet restore Cmsify.slnx --locked-mode
dotnet build Cmsify.slnx --configuration Release --no-restore -p:Version="0.0.0-dry-run" -p:InformationalVersion="0.0.0-dry-run+0000000000000000000000000000000000000000"
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
mkdir -p /tmp/cmsify-dry-run/nuget
dotnet pack src/Cmsify.Contracts/Cmsify.Contracts.csproj --configuration Release --no-build --output /tmp/cmsify-dry-run/nuget -p:CmsifyReleaseBuild=true -p:PackageVersion="0.0.0-dry-run" -p:RepositoryCommit="0000000000000000000000000000000000000000" -p:IncludeSymbols=false
cd sdk/typescript && npm ci && npm run generate:check && npm run typecheck && npm test && npm run build && cd ../..
docker buildx build --platform linux/amd64 --load --tag cmsify-api:dry-run --build-arg BUILD_VERSION=0.0.0-dry-run --build-arg BUILD_INFORMATIONAL_VERSION=0.0.0-dry-run+0000000000000000000000000000000000000000 --build-arg BUILD_SOURCE_REVISION=0000000000000000000000000000000000000000 --file src/Cmsify.Api/Dockerfile .
docker buildx build --platform linux/amd64 --load --tag cmsify-admin:dry-run --build-arg BUILD_VERSION=0.0.0-dry-run --build-arg BUILD_INFORMATIONAL_VERSION=0.0.0-dry-run+0000000000000000000000000000000000000000 --build-arg BUILD_SOURCE_REVISION=0000000000000000000000000000000000000000 --file src/Cmsify.Admin/Dockerfile .
docker network create cmsify-smoke-dry-run
docker run -d --network cmsify-smoke-dry-run --name cmsify-smoke-postgres-dry -e POSTGRES_DB=cmsify -e POSTGRES_USER=cmsify -e POSTGRES_PASSWORD=cmsify-smoke postgres:17
for i in $(seq 1 30); do docker exec cmsify-smoke-postgres-dry pg_isready -U cmsify -d cmsify > /dev/null 2>&1 && break; sleep 2; done
docker run -d --network cmsify-smoke-dry-run --name cmsify-smoke-api-dry -p 18080:8080 -e ASPNETCORE_URLS=http://+:8080 -e ConnectionStrings__Cmsify="Host=cmsify-smoke-postgres-dry;Port=5432;Database=cmsify;Username=cmsify;Password=cmsify-smoke" -e Cors__AllowedOrigins__0=http://localhost -e Seed__Admin__Email=smoke@example.com -e Seed__Admin__DisplayName="Smoke Admin" -e Seed__Admin__Password="Cmsify-smoke-test-only-1!" cmsify-api:dry-run
curl --fail --retry 20 --retry-delay 3 --retry-connrefused http://127.0.0.1:18080/health/ready
docker run -d --network cmsify-smoke-dry-run --name cmsify-smoke-admin-dry -p 18081:8080 -e ASPNETCORE_URLS=http://+:8080 -e Admin__ApiBaseUrl=http://cmsify-smoke-api-dry:8080 cmsify-admin:dry-run
curl --fail --retry 20 --retry-delay 3 --retry-connrefused http://127.0.0.1:18081/login
docker rm -f cmsify-smoke-postgres-dry cmsify-smoke-api-dry cmsify-smoke-admin-dry
docker network rm cmsify-smoke-dry-run
```

Expected: every command succeeds; both `curl` calls return without a `--fail` error. This proves the exact commands the new `build-test-and-package` job runs actually work, without needing a real tag push to find out. This is a manual, throwaway dry run — do not commit anything from `/tmp/cmsify-dry-run` or leave the `cmsify-api:dry-run`/`cmsify-admin:dry-run` local images or `dry-run` Docker resources behind (clean up with `docker rmi cmsify-api:dry-run cmsify-admin:dry-run` after).

- [ ] **Step 4: Report readiness for a real release**

No commit for this task — it's verification only. Once Steps 1-3 pass, the plan is complete: the pipeline is ready for its actual end-to-end test, which is pushing the next real version tag (per the spec's Verification section, there is no meaningful way to fully rehearse a release pipeline offline — the `promote` job's manual approval gate is the safety net if something surfaces only at that stage).
