# Task 4 report — licenses, versions, and immutable tag promotion

## Status

Implemented and committed as the Task 4 release-engineering change set. No package, image, Git tag, GitHub Release, merge, or remote publication was created locally.

RTK was unavailable because it could not determine `HOME`; per the task instruction, native commands were used and `HOME` was not reassigned.

## Implementation

- Replaced branch/main automatic publishing and the separate npm publisher with one tag-only `publish-cmsify.yml` workflow.
  - `resolve` validates `vX.Y.Z` and prerelease tags (for example `v1.0.0-rc.1`) and resolves the tag to one immutable commit SHA.
  - `build` checks out that SHA, builds all candidate artifacts once, creates NuGet/npm/OCI archives, generates SPDX SBOMs and checksums, verifies the candidate layout, and uploads one candidate artifact.
  - Clean consumers install that downloaded candidate in a .NET 10 project and in Node 20/22 through Task 3's existing packed-consumer script. The script now accepts `CMSIFY_CLIENT_TARBALL`, so candidate verification does not repack it.
  - `certify` downloads/checksums the candidate and creates GitHub build provenance for the checksum subjects.
  - `promote` is behind the `release` environment; it checks out only the verified source helper, downloads/checksums the candidate, and pushes NuGet, npm, and OCI layouts without any build/pack command. It creates the GitHub Release from checksums, image digests, manifest, and SBOM assets.
  - Every action newly used in the release workflow is pinned to a full commit SHA.
- Added executable Node release checks:
  - `verify-release-contract.mjs` parses the checked-in release/package metadata and fails on branch publication, unpinned actions, metadata/license mismatches, missing consumers, or a promotion rebuild.
  - `validate-release-tag.mjs` validates tag SemVer/source SHA and fails closed for a future `0.x` tag beyond the current `0.1.3` published baseline unless its matching checked-in upgrade fixture manifest exists.
  - `verify-release-artifacts.mjs` checks exact archive count/layout, NuGet MIT/version/source commit, packed npm MIT/Node/version metadata, OCI layout files, SPDX documents, manifest, and checksums.
- Removed GitVersion-based source versioning. Local source builds are `0.0.0-local`; the tag workflow supplies the release version.
- Declared the three public .NET packages MIT and included MIT license text in their archives. `@cmsify/client` is MIT, carries its own MIT license file, and declares Node `>=20`. The .NET SDK projects target `net10.0` only. The repository license remains AGPL and both OCI Dockerfiles label API/Admin images `AGPL-3.0-or-later` with tag version and source revision.
- Updated changelog and release/operations/package documentation to describe the tag-only policy.

## TDD evidence

| Stage | Command | Result |
| --- | --- | --- |
| RED | `node --test tests/release-contract/verify-release-contract.test.mjs` | Failed as expected because `verify-release-contract.mjs` did not exist; the current automatic publication workflow was then reported invalid by the implemented verifier. |
| RED | `node --test tests/release-contract/verify-release-artifacts.test.mjs` | Failed as expected because `verify-release-artifacts.mjs` did not exist. |
| RED | `npm test -- --run test/clean-consumer.test.ts` | Failed as expected: a supplied missing `CMSIFY_CLIENT_TARBALL` was ignored and the script repacked source. |
| RED | `node --test tests/release-contract/verify-release-contract.test.mjs` | The added mutable-promotion case failed before job-body parsing was corrected. |
| RED | `node --test tests/release-contract/validate-release-tag.test.mjs` | The current `v0.1.3` baseline was incorrectly treated as a future `0.x` tag; the comparison was then narrowed to versions newer than `0.1.3`. |
| GREEN | `node --test tests/release-contract/verify-release-contract.test.mjs tests/release-contract/verify-release-artifacts.test.mjs tests/release-contract/validate-release-tag.test.mjs` | Passed: 9/9. It executes the verifier against real files plus fixtures covering automatic branch publication, unpinned actions, promotion rebuilds, metadata mismatch, missing consumers, missing candidate layout, accepted stable/prerelease tags, and the fail-closed future-`0.x` rule. |
| GREEN | `npm test -- --run test/clean-consumer.test.ts` | Passed: 2/2. |

## Validation

| Check | Result |
| --- | --- |
| `node scripts/release/verify-release-contract.mjs` | Passed. The checked-in workflow/package contract is valid. |
| `git diff --check` | Passed. |
| `dotnet restore Cmsify.slnx --verbosity minimal` | Passed. |
| `dotnet build Cmsify.slnx --configuration Release --no-restore --verbosity minimal` | Passed: 0 warnings, 0 errors. |
| `dotnet pack` for Contracts, Client, and DistributedCaching with `-p:PackageVersion=1.0.0-rc.1` | Passed. Three `.nupkg` archives and symbols were produced locally. Inspecting the archives showed version `1.0.0-rc.1`, `net10.0`, `<license type="expression">MIT</license>`, and `LICENSE-MIT.txt`. |
| Clean .NET consumer | Passed. A new temporary .NET 10 console project installed `SyntaxCircus.Cmsify.Client` `1.0.0-rc.1` and Contracts from the candidate directory and built with 0 warnings/errors. |
| TypeScript `npm ci` | Passed after an elevated retry; the sandbox had denied the configured user npm cache. npm reported existing dependency audit findings (1 low, 1 moderate, 5 high, 1 critical), not caused by this change. |
| TypeScript `npm run generate:check`, `typecheck`, `test`, `build`, `test:consumer` | Passed in the complete prior run: live OpenAPI check, typecheck, 39 tests before the added release-consumer test / 40 tests after it, build, and clean packed consumer. A supplied candidate tarball also passed the reused clean-consumer check. |
| Packed npm archive inspection | Passed. The local archive contained `package/LICENSE`, public `dist` files, version `0.0.0-local`, MIT, and Node `>=20`. The tag workflow changes its package version to the validated tag before its single pack. |
| Full `dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal` | Started successfully; Core 52/52, SDK 38/38, Admin 18/18, and Infrastructure 37/37 passed. API Testcontainers completion was not available locally because Docker engine access is denied. |
| OCI build/layout/smoke and generated SPDX SBOM verification | Not runnable locally: `docker version` returned `permission denied while trying to connect to ... docker_engine`. These are build-only workflow gates that load/inspect the OCI layouts, labels, SPDX JSON, checksums, and candidate manifest before attestation/promotion. |
| Node 20/22 local consumers | Not runnable locally: only Node `v24.14.1` is installed. The workflow explicitly runs the reusable candidate consumer check in a Node 20/22 matrix. |

## Source/version/license/SBOM/provenance/digest assertions

- Source version assertion: `Directory.Build.props` defaults all source builds to `0.0.0-local`; the workflow takes the sole public version from validated tag text.
- Source SHA assertion: `resolve` dereferences the tag to a 40-character commit SHA; builds check out that SHA; the candidate manifest, OCI labels, NuGet repository metadata, and artifact verifier all require that SHA.
- License assertions: root/server source is AGPL; API/Admin OCI labels require `AGPL-3.0-or-later`; each public .NET package and packed npm archive must be MIT, with .NET package license text and TypeScript package `LICENSE` included.
- SBOM assertions: the workflow emits four SPDX JSON documents (NuGet, npm, API OCI, Admin OCI); the artifact verifier requires valid `spdxVersion` documents.
- Checksum/digest assertions: `SHA256SUMS` covers every candidate archive, SBOM, and manifest before upload; certify and promote run `sha256sum --check`; promotion records pushed immutable image digests as `OCI-DIGESTS` and attaches it to the GitHub Release.
- Provenance assertion: the certification job uses pinned `actions/attest-build-provenance` with the candidate checksum subjects and required `attestations: write`/`id-token: write` permissions.

## Files changed

Release workflow, npm publication workflow removal, source version plumbing, Dockerfiles/local Docker helper, package metadata/readmes/licenses, TypeScript consumer script/tests, release verifier scripts/tests, changelog, solution/package pins, and release/operations documentation.

## Self-review

Reviewed the complete `0a8d45e390e313d2d8c78dcfe26178dc71a9acd1..f1bd72535b7bd2d4fede4ca4c4cf66c6df7d7a74` Task 4 diff after the initial local commit; `git diff --check` was clean. Confirmed:

- no branch workflow can publish/tag through the replaced release workflow;
- no promotion command repacks/builds artifacts;
- promotion uses only the checked/downloaded candidate and verifies its checksums;
- public package/license/runtime floors match the binding requirements;
- generated TypeScript sources were not edited; generated `dist` and all temporary validation outputs were removed;
- no release side effect was invoked locally.

## Concerns

1. Docker is inaccessible locally, so an actual OCI build/load/SBOM smoke and an end-to-end GitHub Actions run were not performed here.
2. The local machine has Node 24 only; Node 20/22 execution is encoded in the release workflow but needs GitHub Actions evidence.
3. GitHub environment protection, registry/npm/NuGet credentials, attestation availability, and release permissions are repository-admin configuration. The workflow fails closed if they are absent; Task 12 owns broad production/governance certification.
4. Existing npm dependency audit findings remain; no dependency versions were changed in this task.
