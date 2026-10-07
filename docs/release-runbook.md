# Release runbook

This runbook describes how a maintainer ships a Cmsify release by pushing a validated SemVer tag, which triggers `publish-cmsify.yml`.

## Preflight

Before tagging, from the exact commit you intend to release:

```powershell
$lockedAssets = (Resolve-Path scripts/qualification/locked-assets.targets).Path
dotnet --version # Qualification uses the global.json SDK version, 10.0.400.
dotnet restore Cmsify.slnx --locked-mode "-p:CustomAfterMicrosoftCommonTargets=$lockedAssets"
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental "-p:CustomAfterMicrosoftCommonTargets=$lockedAssets"
dotnet test --solution Cmsify.slnx --configuration Release --no-build --max-parallel-test-modules 1 --minimum-expected-tests 1 "-p:CustomAfterMicrosoftCommonTargets=$lockedAssets"
```

SDK 10.0.400 defaults Admin's implicit ASP.NET Core Assets package to 10.0.11,
while the unchanged lock requires 10.0.12. The tracked qualification target sets
that existing locked version through the SDK's supported framework-reference
hook; it does not modify the SDK, dependency versions or locks. Both release and
ordinary .NET workflows use the same target. Its SHA-256 is
`d8e96e62202728ae251a71bbf3e226f9474108f9f6666dbd6629342f5043ad4b`, identical
to the target used for local Windows/Linux qualification.

`global.json` selects Microsoft.Testing.Platform. Use `--solution` or `--project`
and its supported test options, including a nonzero minimum expected count;
legacy positional solution and VSTest filter commands are incompatible. Capacity
selection uses `--filter-trait "Category=Capacity"`. Coverage uses `--coverage
--coverage-output-format cobertura --results-directory artifacts/coverage` with
the same solution command. Preserve actual failed/skipped counts and require all
source/package gates before release approval. See [candidate evidence](evidence/2026-10-07-host-workspace-visibility.md)
for the exact SDK/lock constraint and measured scope.

Add a dated `## [X.Y.Z] - YYYY-MM-DD` section to `CHANGELOG.md` — `scripts/release/validate-release-tag.mjs` refuses the tag without one (an `## [Unreleased]` placeholder does not count).

Before the first release containing `SyntaxCircus.Cmsify.Core` and `SyntaxCircus.Cmsify.Infrastructure`, verify that NuGet ownership/reservation and trusted-publisher scope permit both IDs. The two engine packages retain AGPL-3.0-or-later; the existing SDK/contracts/components package licenses remain unchanged. This registry prerequisite requires maintainer verification and is not proven by local packing.

## Tag and let the pipeline run

```powershell
git tag vX.Y.Z <commit>
git push origin vX.Y.Z
```

`publish-cmsify.yml` runs three jobs:

1. **`resolve`** — validates the tag and changelog entry, resolves the version and source SHA.
2. **`build-test-and-package`** — builds and tests the .NET solution, packs eight NuGet packages (Core, Infrastructure, Infrastructure.Sqlite, Contracts, Client, DistributedCaching, Components and Theme) and the npm SDK package, qualifies the three engine `.nupkg` files in separate clean consumers against real PostgreSQL and native SQLite, builds both Docker images locally, and smoke-tests them together (`docker run` + `curl` against `/health/ready` for the API, `/login` for Admin). Nothing is pushed, signed, or published in this job — if qualification or smoke tests fail, promotion cannot run. See [engine package qualification](engine-packages.md) for the exact gate and limitations.
3. **`promote`** (`environment: release`, requires manual approval) — pushes both smoke-tested images to Docker Hub (plus `:latest` for a stable release), signs them with Cosign, generates an SBOM for each with Syft, publishes the NuGet packages and npm package via trusted OIDC publishing, and creates the GitHub Release with the SBOMs attached.

Approve the `release` environment deployment in the Actions run when you're satisfied the smoke test passed and the build looks right. Nothing publishes anywhere until that approval.

## If a release is only partially published

If `promote` fails partway through (for example, Docker images pushed but NuGet push failed), do not retry the same tag — some registries were already written to under that immutable version. Record what succeeded, then cut the next patch version as a new, complete release. Do not reuse or repair a partially-published version tag.

Use the [rollback runbook](rollback-runbook.md) if a deployed version needs to be rolled back after release.
