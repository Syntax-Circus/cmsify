# Release runbook

This runbook describes the release evidence required before an authorized maintainer pushes a validated SemVer tag to trigger the tracked `publish-cmsify.yml` workflow. It does not authorize a publish, promotion, signing, tag, or release.

## Roles and prerequisites

- The release operator records the exact tag, source SHA, candidate artifact hashes, OCI manifest digests, and workflow run URL.
- The approver supplies protected approval evidence when a breaking `/api/v1` change or an emergency exception is requested.
- The backup custodian verifies the matched PostgreSQL, media, and Admin Data Protection-key backup manifest before deployment.

GitHub environment protection, registry permissions, npm/NuGet trusted publishing, advisory enablement, Cosign identity policy, and CODEOWNERS activation are unverified prerequisites. A repository administrator must verify them in the hosted systems before a release; this file does not claim they are configured.

The npm package identity is `@syntaxcircus/cmsify-client`, under the existing `syntaxcircus` npm organization. npm creates a package on its first publish, while trusted publishing is configured from that package's settings. Bootstrap a previously unpublished package exactly once with a reviewed `0.0.0-bootstrap.0` tarball, public access, and the non-default `bootstrap` tag; never assign that bootstrap version to `latest`. After the package exists, configure its GitHub Actions trusted publisher for organization `Syntax-Circus`, repository `cmsify`, workflow `publish-cmsify.yml`, environment `release`, and the `npm publish` action. The protected workflow deliberately leaves `registry-url` unset in `actions/setup-node`, because its generated token-style `.npmrc` can prevent npm from exchanging the GitHub OIDC token. All real versions, beginning with the post-bootstrap release, publish only through that trusted workflow.

The [Task 12 evidence ledger](evidence/task-12-local-verification.json) keeps every external gate false until its declared immutable inputs are available. Its exact `nextCommand` entries call `scripts/release/verify-task-12-external-gate.ps1`; populate those inputs only from the candidate run and retained hosted evidence, never from an unrelated or reconstructed run.

## Preflight and certify

From the exact committed source, record the output of these checked commands:

```powershell
node --test tests/release-contract/*.test.mjs
node scripts/release/verify-release-contract.mjs
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs verify-release-baseline --fixture tests/upgrade/fixtures/v0.1.3
```

Confirm the tag resolves to the recorded source SHA, every lock resolves as documented, and the candidate workflow follows `resolve` → `build` → parallel `artifact-smoke`, `candidate-accessibility`, `dotnet-consumer`, `node-consumer`, and `upgrade-rollback` → `certify` → `promote`. Preserve the generated release manifest, `SHA256SUMS`, SPDX files, accessibility output, upgrade diagnostics, package content hashes, and each immutable digest as evidence.

Buildx produces OCI-layout tarballs, not Docker-save archives. Certification jobs must import those layouts with the repository loader after checking `SHA256SUMS`; direct `docker load` is unsupported for this artifact format:

```powershell
node scripts/release/load-oci-candidate.mjs load `
  --archive artifacts/oci/cmsify-api.oci.tar `
  --manifest artifacts/release-manifest.json `
  --kind api `
  --version $version
```

The original OCI archive is the certified release artifact and the only promotion source. The loader validates regular non-link archive and manifest paths, the manifest kind/ref/version/digest, and the selected OCI manifest and config blobs before Docker access. Pinned Skopeo 1.22.2 converts that verified candidate offline into run-owned Docker transport scratch with no network or Docker socket access and with the source archive mounted read-only. Docker loads only that transport scratch for `linux/amd64`; the loader then verifies the config digest, ordered DiffIDs, platform, all required OCI labels, and the exact canonical tag against the original archive evidence. The scratch is deleted on success and failure and is never checksummed, uploaded, or promoted. The loader never rebuilds or externally pulls the candidate, and every create-then-fail path performs bounded run-owned cleanup.

Abort before promotion if any command fails, a source SHA/tag/digest differs, a required protected approval is absent, a backup manifest is incomplete, public restore remains unproved, or any candidate would be rebuilt. Do not rebuild a candidate to repair evidence and do not publish or promote without the required approval; restart the authorized process from a newly recorded candidate instead.

If promotion partially publishes an immutable version, record every accepted registry write and stop. Do not move that tag to a repair commit, rebuild a missing ecosystem artifact under the same release claim, or create a GitHub Release that implies a complete same-source tuple. Correct the release machinery through review and use the next patch version for a new complete candidate. `v0.2.0` is the historical example: its NuGet submissions and OCI images were accepted before npm rejected the unowned `@cmsify/client` identity, so it has no GitHub Release and must not be reused; the corrected complete candidate is `v0.2.1`.

## Promote only certified bytes

The promotion job must copy the certified OCI descriptor by digest and compare the remote digest before package publication. Its pinned ORAS invocation uses the stable `--oci-layout path@digest` and `--from-oci-layout path@digest` forms; the experimental `--oci-layout-path` forms are not promotion evidence because they can fall through to a registry lookup. Promotion must not rebuild an image. Before restoring traffic to a deployment, verify a matched database/media backup, the retained prior image digest, `/health/live`, `/health/ready`, Admin sign-in, representative authenticated reads, and representative media downloads. The public restore gate for `SyntaxCircus.Http.Resilience` remains user-owned until exact public bytes and clean restore evidence exist.

Use [Rollback runbook](rollback-runbook.md) when an abort criterion is met during or after deployment.
