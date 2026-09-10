# Release runbook

This runbook describes how a maintainer ships a Cmsify release by pushing a validated SemVer tag, which triggers `publish-cmsify.yml`.

## Preflight

Before tagging, from the exact commit you intend to release:

```powershell
dotnet restore Cmsify.slnx --locked-mode
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
