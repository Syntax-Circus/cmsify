# Cmsify moving-baseline upgrade and rollback harness

This harness proves that the checked-in latest stable `0.1.x` database and media fixture can start on its exact published image, upgrade to one exact v1 candidate, and roll back by restoring a matched pre-upgrade backup into clean storage. It is test infrastructure, not a production backup tool. Production operators should follow the [production upgrade and rollback runbook](../../docs/operations.md#v01x-to-v1-upgrade-and-rollback).

The current recorded baseline is `0.1.3`. The immutable source, image, platform, and dependency digests are in [`fixtures/v0.1.3/manifest.json`](fixtures/v0.1.3/manifest.json); every fixture payload is covered by [`fixtures/v0.1.3/SHA256SUMS`](fixtures/v0.1.3/SHA256SUMS).

## Prerequisites and safety boundary

- Node.js 22.
- Docker Engine running Linux containers with `linux/amd64` support.
- Docker Compose v2, available as `docker compose`.
- A clean, committed candidate source revision. The full 40-character `git rev-parse HEAD` must be embedded in the image.
- Network access for Docker to pull the digest-pinned baseline, PostgreSQL, and MinIO images and for the candidate Dockerfile's normal public NuGet restore.

Run every command from the repository root. Do not substitute an alternate Dockerfile, private package, local NuGet feed, or mutable baseline tag. The fixture contains only public synthetic test identities and credentials; it must never contain production data or secrets. The fixture credentials are deliberately recoverable and are accepted only by the isolated harness configuration. Never copy them into a deployed environment.

Compose gives every owned container, volume, and network both of these labels:

- `io.syntaxcircus.cmsify.upgrade-test=true`
- `io.syntaxcircus.cmsify.upgrade-run=<generated-run-id>`

The harness uses an internal Docker network, maps the synthetic webhook host to a documentation-only address, and does not publish service ports. Cleanup verifies both labels and may remove only resources owned by that run. Never use `docker system prune`, an unlabeled name prefix, or another global cleanup command for this harness.

## Fixture inventory

The version directory is an immutable, reviewable snapshot of one published baseline:

| File | Purpose |
| --- | --- |
| [`manifest.json`](fixtures/v0.1.3/manifest.json) | Schema version, published baseline version and source SHA, exact linux/amd64 API/PostgreSQL/MinIO repository/tag/digest tuples, deterministic generator schema/version and checked-in seed path/hash, required payload paths, and required scenario coverage. |
| [`SHA256SUMS`](fixtures/v0.1.3/SHA256SUMS) | Canonical, sorted SHA-256 inventory for every other fixture payload. The verifier rejects missing, extra, linked, or changed files. |
| [`database.sql`](fixtures/v0.1.3/database.sql) | Deterministically normalized PostgreSQL dump created through the exact published API and augmented only by the reviewed historical seed. |
| [`expected.json`](fixtures/v0.1.3/expected.json) | Stable IDs, timestamps, relationships, API expectations, media hashes, authentication fixtures, migration expectations, and package provenance consumed by the shared assertion registry. Its tokens/passwords are synthetic test credentials, not secrets. |
| [`media/...-fixture.txt`](fixtures/v0.1.3/media/cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1-fixture.txt) | Exact text media payload used for byte-for-byte retrieval and matched-backup checks. |
| [`media/...-pixel.png`](fixtures/v0.1.3/media/cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2-pixel.png) | Exact one-pixel PNG used for binary retrieval and matched-backup checks. |

Other harness inputs are not fixture payloads: [`seed/v0.1.3.sql`](seed/v0.1.3.sql) supplies deterministic historical rows during regeneration, and [`compose.yml`](compose.yml) defines the isolated PostgreSQL, MinIO, published baseline API, and candidate API topology. The checked-in generator hashes the exact seed bytes into the strict `manifest.generation` object and copies that object into `expected.json` provenance; `verify-fixture` and rehearsal preflight re-hash the seed and reject drift. The generator schema/version, seed path/hash, expected provenance, fixture payload checksums, and fixture output contain no wall-clock generation field and must never be hand-edited. Unit tests exercise validation and failure behavior without Docker; [`integration/rehearsal.test.mjs`](integration/rehearsal.test.mjs) deliberately runs two clean end-to-end rehearsals when opted in.

The manifest's required scenarios are workspaces, permissions, templates, inline acyclic components, immutable choice revisions, content versions, schedules/effective ranges, media, webhooks, audit, authentication, and provenance. Removing a scenario or weakening expected data is a fixture contract change, not routine regeneration.

## Verify the checked-in fixture

PowerShell:

```powershell
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
```

POSIX shell:

```sh
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
```

`verify-fixture` is a fast, non-Docker integrity/schema/coverage check. `generate-fixture --check` starts the exact digest-pinned historical topology, recreates the fixture in a temporary directory, validates it through the published API, and requires byte-for-byte equality with the checked-in tree. Neither successful check modifies the fixture.

## Build and rehearse an exact candidate

Use a version without SemVer build metadata; the source SHA is added separately to the informational version. These commands use the normal production API Dockerfile and its public NuGet sources.

PowerShell:

```powershell
$candidateVersion = '1.0.0-task9'
$candidateImage = "syntaxcircus/cmsify-api:$candidateVersion"
$sourceSha = (git rev-parse HEAD).Trim()

docker build --platform linux/amd64 --provenance=false `
  --build-arg "BUILD_VERSION=$candidateVersion" `
  --build-arg "BUILD_INFORMATIONAL_VERSION=$candidateVersion+$sourceSha" `
  --build-arg "BUILD_SOURCE_REVISION=$sourceSha" `
  --tag $candidateImage `
  --file src/Cmsify.Api/Dockerfile .

node eng/upgrade-tests/cli.mjs rehearse `
  --fixture tests/upgrade/fixtures/v0.1.3 `
  --candidate-image $candidateImage `
  --candidate-version $candidateVersion `
  --candidate-source-sha $sourceSha
```

POSIX shell:

```sh
candidate_version='1.0.0-task9'
candidate_image="syntaxcircus/cmsify-api:${candidate_version}"
source_sha="$(git rev-parse HEAD)"

docker build --platform linux/amd64 --provenance=false \
  --build-arg "BUILD_VERSION=${candidate_version}" \
  --build-arg "BUILD_INFORMATIONAL_VERSION=${candidate_version}+${source_sha}" \
  --build-arg "BUILD_SOURCE_REVISION=${source_sha}" \
  --tag "${candidate_image}" \
  --file src/Cmsify.Api/Dockerfile .

node eng/upgrade-tests/cli.mjs rehearse \
  --fixture tests/upgrade/fixtures/v0.1.3 \
  --candidate-image "${candidate_image}" \
  --candidate-version "${candidate_version}" \
  --candidate-source-sha "${source_sha}"
```

The preflight inspects the candidate tag once, then binds the run to its immutable image ID. It requires `linux/amd64`, the exact OCI version and revision labels, and the exact build informational version. A later retag cannot change the image already selected for that run. In the release workflow, the original OCI archive is the certified release artifact and the already-built OCI layout is exposed under its canonical tag only by `scripts/release/load-oci-candidate.mjs`. Pinned Skopeo converts the verified candidate offline into run-owned Docker transport scratch with no network or Docker socket access and a read-only source mount. Docker loads only that transport scratch; the loader verifies the config digest, ordered DiffIDs, platform, all required OCI labels, and the canonical tag before rehearsal. The scratch is deleted on success and failure and is never checksummed, uploaded, or promoted. The release rehearsal never rebuilds or externally pulls the candidate.

For the release-style repeatability proof, the opt-in integration test runs the same rehearsal twice with separately generated run IDs:

```powershell
$env:CMSIFY_UPGRADE_TEST = '1'
$env:CMSIFY_UPGRADE_CANDIDATE_IMAGE = $candidateImage
$env:CMSIFY_UPGRADE_CANDIDATE_VERSION = $candidateVersion
$env:CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA = $sourceSha
node --test tests/upgrade/integration/rehearsal.test.mjs
```

```sh
CMSIFY_UPGRADE_TEST=1 \
CMSIFY_UPGRADE_CANDIDATE_IMAGE="${candidate_image}" \
CMSIFY_UPGRADE_CANDIDATE_VERSION="${candidate_version}" \
CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA="${source_sha}" \
node --test tests/upgrade/integration/rehearsal.test.mjs
```

## What each phase proves

Every phase is mandatory and has a bounded command/readiness deadline. A skipped, timed-out, cancelled, partially completed, or cleanup-failed run cannot report success.

1. `preflight` verifies tools, fixture checksums/coverage/provenance, exact digest-pinned baseline images, and exact candidate identity before creating persistent resources.
2. `restore-fixture` restores the checked-in PostgreSQL and media fixture into clean run-owned volumes.
3. `baseline` starts only the exact published API and validates readiness, authentication/authorization, domain behavior, database relationships/migrations, and exact media bytes.
4. `backup` stops the baseline API, creates a matched database/media backup, inventories every media object, and writes a checksum manifest fenced to the run and baseline.
5. `upgrade` starts only the candidate against the restored baseline state and lets its normal entry point apply migrations.
6. `candidate` reruns the required invariants, validates v1-only migration/media/secret behavior, and writes then reads an ETag-protected canary.
7. `backup-reverify` rechecks the original matched backup immediately before any upgraded state is discarded.
8. `discard-upgraded-state` stops the candidate and deletes only the run-owned v1-written database and media volumes.
9. `restore-backup` creates clean volumes and restores both members of the same matched pre-upgrade backup.
10. `rollback` starts only the exact published baseline API, reruns the full baseline assertion registry, verifies exact media bytes, and proves the candidate canary is absent.
11. `cleanup` removes only resources carrying both ownership labels and deletes the temporary environment file.

The `0.1.3` binary is never started against v1-written state. Rollback is proven only after both database and media are restored into clean storage.

## Diagnostics and cleanup audit

Each run writes `artifacts/upgrade-tests/<run-id>/report.json` atomically after phase transitions. It contains bounded phase status, exact image identities, completed/failed safe assertion names, readiness observations, fixture digest, and matched-backup manifest digest. Failures also attempt to retain `docker-diagnostics.json` with capped, allow-listed excerpts grouped by PostgreSQL, MinIO, baseline API, and candidate API plus safe applied migration IDs/state. Raw response bodies, tokens, environment dumps, credentials, raw database rows, fixture media bytes, and unrestricted service logs are never retained.

The run directory also contains the synthetic matched fixture backup used by rollback. Treat the entire directory as test-only controlled data; do not substitute a production backup and do not publish it as general logs. CI uploads `artifacts/upgrade-tests/**` only when the workflow fails and retains it for 14 days. The temporary `.env` file under `tests/upgrade/.runs` is mode-restricted and removed during cleanup.

Start diagnosis with the failed phase and `errorCode` in `report.json`. Confirm the candidate image ID/source labels, baseline digests, last passed phase, backup verification evidence, and cleanup status. A backup-integrity failure means rollback is unproved; preserve the run directory and investigate without starting the old image on candidate-written volumes.

Audit cleanup after every run:

```powershell
docker ps -a --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker volume ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker network ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
```

```sh
docker ps -a --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker volume ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
docker network ls --filter 'label=io.syntaxcircus.cmsify.upgrade-test=true'
```

Headers with no rows are the expected result. If resources remain, record their exact IDs and both label values before removing only those resources. Retain the report and diagnostic directory. Do not broaden cleanup to unrelated Docker resources.

## Refresh after a stable `0.1.x` publication

Refresh the moving baseline only after the newer `0.1.x` GitHub Release and linux/amd64 Docker image are actually published. Do not create a fixture from an unpublished build, a prerelease, `latest`, or the candidate currently being certified. The commands below use published `0.1.4` and `tests/upgrade/fixtures/v0.1.4` as the concrete example; do not run them until `0.1.4` is a real stable publication.

1. Record the published version, peeled tag source SHA, and immutable linux/amd64 API digest. Resolve and record exact PostgreSQL and MinIO linux/amd64 digests as well.
2. Copy the prior versioned fixture and seed to new versioned paths as a review starting point. The following commands fail rather than overwrite an existing `v0.1.4` fixture or seed.

   PowerShell:

   ```powershell
   $publishedBaselineVersion = '0.1.4'
   $fixturePath = "tests/upgrade/fixtures/v$publishedBaselineVersion"
   $seedPath = "tests/upgrade/seed/v$publishedBaselineVersion.sql"
   if ((Test-Path -LiteralPath $fixturePath) -or (Test-Path -LiteralPath $seedPath)) {
     throw "Refusing to overwrite the $publishedBaselineVersion fixture or seed."
   }
   Copy-Item -LiteralPath 'tests/upgrade/fixtures/v0.1.3' -Destination $fixturePath -Recurse
   Copy-Item -LiteralPath 'tests/upgrade/seed/v0.1.3.sql' -Destination $seedPath
   ```

   POSIX shell:

   ```sh
   published_baseline_version='0.1.4'
   fixture_path="tests/upgrade/fixtures/v${published_baseline_version}"
   seed_path="tests/upgrade/seed/v${published_baseline_version}.sql"
   if [ -e "${fixture_path}" ] || [ -e "${seed_path}" ]; then
     echo "Refusing to overwrite the ${published_baseline_version} fixture or seed." >&2
     exit 1
   fi
   cp -R 'tests/upgrade/fixtures/v0.1.3' "${fixture_path}"
   cp 'tests/upgrade/seed/v0.1.3.sql' "${seed_path}"
   ```

3. Update the generator's version-specific synthetic constants and seed path for `0.1.4`, including its generator version/schema when the generation contract changes. Supply the exact published baseline version/source and linux/amd64 API, PostgreSQL, and MinIO digests to the checked-in generator inputs, and update its expected scenario inputs. Let the generator write the strict generation/seed provenance into both metadata documents and regenerate `database.sql`, media output, and `SHA256SUMS`; never hand-edit those generated results to make comparison pass.
4. Update every active authoritative fixture reference—the generator, integration test, branch workflow, dedicated workflow, release workflow/verifier, and operator documentation—from `v0.1.3` to `v0.1.4`. Preserve historical evidence that intentionally describes an older run. Use this inventory to find active references for review:

   ```powershell
   rg -n 'v0\.1\.3|0\.1\.3' eng/upgrade-tests tests/upgrade .github/workflows scripts/release docs/operations.md
   ```

   ```sh
   rg -n 'v0\.1\.3|0\.1\.3' eng/upgrade-tests tests/upgrade .github/workflows scripts/release docs/operations.md
   ```

5. Extend tests and expected assertions for schema/domain changes while preserving every required scenario and the shared baseline/rollback assertion strength.
6. Generate the concrete `v0.1.4` fixture once, then prove deterministic regeneration and integrity.

   PowerShell:

   ```powershell
   $publishedBaselineVersion = '0.1.4'
   $fixturePath = "tests/upgrade/fixtures/v$publishedBaselineVersion"
   node eng/upgrade-tests/cli.mjs generate-fixture --fixture $fixturePath
   node eng/upgrade-tests/cli.mjs generate-fixture --fixture $fixturePath --check
   node eng/upgrade-tests/cli.mjs verify-fixture --fixture $fixturePath
   ```

   POSIX shell:

   ```sh
   published_baseline_version='0.1.4'
   fixture_path="tests/upgrade/fixtures/v${published_baseline_version}"
   node eng/upgrade-tests/cli.mjs generate-fixture --fixture "${fixture_path}"
   node eng/upgrade-tests/cli.mjs generate-fixture --fixture "${fixture_path}" --check
   node eng/upgrade-tests/cli.mjs verify-fixture --fixture "${fixture_path}"
   ```

7. Run the complete unit/release-contract set and two-pass exact-image rehearsal against the next candidate. Confirm the post-run ownership-label audit is empty.
8. Use `verify-release-baseline` during certification to prove the refreshed `v0.1.4` fixture matches the latest already-published stable `0.1.x` GitHub release, peeled source commit, and Docker Hub linux/amd64 API digest.

   PowerShell:

   ```powershell
   $fixturePath = 'tests/upgrade/fixtures/v0.1.4'
   $candidateVersion = '1.0.0-rc.1'
   node eng/upgrade-tests/cli.mjs verify-release-baseline --fixture $fixturePath --candidate-version $candidateVersion
   ```

   POSIX shell:

   ```sh
   fixture_path='tests/upgrade/fixtures/v0.1.4'
   candidate_version='1.0.0-rc.1'
   node eng/upgrade-tests/cli.mjs verify-release-baseline --fixture "${fixture_path}" --candidate-version "${candidate_version}"
   ```

   Commit the regenerated fixture, updated authoritative references/digests, harness changes, and reviewable provenance together. Once `0.1.4` is published, no later `0.1.x` or v1 candidate may promote while the authoritative fixture still records `0.1.3`.

The dedicated [upgrade workflow](../../.github/workflows/upgrade-rollback.yml) performs the same fixture verification, deterministic regeneration check, exact candidate build, two-pass rehearsal, and failure-only diagnostics upload. The release workflow loads its already-built OCI archive, verifies the current published baseline, and requires this rehearsal before protected promotion.
