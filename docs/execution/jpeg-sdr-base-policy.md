# Opt-in JPEG SDR-base processing — local implementation

Related host decision: Puppies Plus Owner selected SDR web images with complete
HDR originals retained. Base Cmsify main4ccabdadee4397cdced461e29e9f3dac79783597,
clean checkout; branch `feature/jpeg-sdr-base-policy`. Qualification below was
collected before commit/push; the Owner subsequently requested a PR handoff.

Add `jpeg-sdr-base-webp-v1` as a separately enabled policy alongside legacy/PNG.
Exact proof: JPEG,8-bit,sRGB,no alpha,UsedSdrBase=true and conversion version
`apple-jpeg-sdr-base-q8-srgb-strip-v1` with original depth8. Use existing three
WebP requirements and24MP bounds. Native format parsing/conversion remains
host-owned; Cmsify verifies manifest/proof/storage/authority, never chooses a frame.

Add opt-in option, immutable registration snapshot, readiness capability validation
and queue negotiation. Disabled hosts must not query an absent JPEG adapter.
Mixed queues continue eligible legacy/PNG jobs during JPEG adapter outages. New
policy enrollment/revisions use existing immutable operation receipts, source hashes,
leases, manifest identities and retention. No schema or HTTP contract changes.

Plan: RED core proof and PostgreSQL enrollment/queue tests; implement minimal opt-in
policy/configuration/worker changes; GREEN focused then full solution checks; update
integration guide/changelog and record measured results. Synthetic fixture headers
exercise orchestration only, not native JPEG decoding. Actual source/native evidence
is retained separately in the Puppies Plus checkout.

## Final local implementation and qualification — 2026-10-23

Strict proof, immutable registration snapshot, optional readiness and third-policy
bounded queue eligibility are implemented. JPEG manifests use version3 and bind
source/conversion metadata; PNG version2 and legacy version1 hashing stay unchanged.
Manifest lookup, variant read and reference protection validate JPEG proof even
after disabling the option. Original-metadata consistency, source/receipt binding,
leases, authority, original bytes and retention remain unchanged.

Review found disabled revision/retry paths probed an absent adapter and a throwing
optional probe could block legacy claims. After RED3, enablement precedes adapter
access (after existing receipt replay), and optional probe exceptions become
unavailable while cancellation propagates. Final static recheck has no actionable
findings; reviewer did not rerun tests or inspect private native-image evidence.

Windows has SDK10.0.401 only: these focused checks are preliminary, not exact
10.0.400 Windows qualification. Linux exact-SDK source qualification uses
`mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c`,
whose SDK reports10.0.400. No product version or global SDK configuration changed.

| Check | Measured result |
| --- | --- |
| Core RED | 1 failed/6 passed |
| Initial PostgreSQL RED | 3 failed/1 passed |
| Manifest-tampering RED | 2 failed: invalid conversion and valid-but-altered dimensions |
| Review regression RED | 3 failed: disabled revision/retry and throwing adapter |
| Final Windows core policy checks | 7 passed,0 failed/skipped;0.740s |
| Final Windows focused infrastructure/registration/PNG checks | 38 passed,0 failed/skipped;15.725s |
| Linux exact-SDK locked restore/nonincremental build | pass;0 warnings/errors;build1m02.22s |
| Linux full solution | 2,111 passed/1 failed/0 skipped;11m47.386s |
| Linux full infrastructure module | passed;2m39.445s |
| Linux original failing query case recheck | 1 passed;7.939s |
| Linux full API module recheck | 571 passed/1 failed/0 skipped;8m47.551s |
| Linux both failing API cases together, unchanged binaries | 2 passed;10.254s |

The full solution timed out opening Docker PostgreSQL at192.168.65.254:63214 in
`QueryApiTests.ResolvedContentList_SelectsTheMostSpecificActiveVersionAndExcludesTheExactEnd`
during fixture seeding. The API rerun passed that case but hit the same connection
timeout at a different port during startup migration in
`WebhookAuditApiTests.ContentCreateEtag_AuthorizesImmediateConditionalUpdate`.
Neither reached a JPEG assertion; both later pass together unchanged. Fixtures
serialize tests and share one process PostgreSQL container, so a simple parallel
connection-string overwrite was not established as the cause.

A direct172.17.0.1 bridge probe failed both cases during Testcontainers resource
reaper initialization (2m03.928s), and was not adopted. Host forwarding remains the
supported local route. **The full suite is not clean locally**: intermittent Docker/
PostgreSQL connectivity under the long API run is an unresolved qualification
limitation. Retain this gate for stable CI/environment qualification before release.
Targeted passes do not erase failed runs or establish their root cause. No API or
test behavior was changed in response to the connectivity failures.

New PostgreSQL checks cover opt-in, source-hash/policy replay conflicts, fresh-worker
enrollment, unavailable/throwing adapters, historical manifests, metadata tampering
and disabled defaults. Full infrastructure checks cover leases/recovery, authority,
readback, references and retention. Synthetic headers prove orchestration, not
native JPEG fidelity. No HTTP/UI contract changed; no SDK regeneration/browser claim.

Other failed attempts: a ready-revision fixture claimed prior HEIC metadata and was
correctly rejected against JPEG. The fixture was corrected to prior JPEG SDR data;
the source-identity guard was preserved. Guessed documentation/wildcard searches
and one atomic documentation-context patch were corrected. Failed logs stay ignored.

## Reproduction and identities

Prefix commands with `rtk proxy`:

1. `dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~EmbeddedJpegSdrPolicyTests`
2. `dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~EmbeddedJpegSdrProcessingTests|FullyQualifiedName~EmbeddedPhotoRegistrationTests|FullyQualifiedName~EmbeddedPngProcessingTests"`
3. `pwsh -NoProfile -File scripts/qualification/Invoke-EmbeddedFileSourceQualification.ps1 -Platform Linux`

Frozen source/evidence: ignored
`artifacts/file-source/linux-d7116559a5d04b3382a4c782f2301743`.
All994 inputs matched before this final evidence prose was appended; final checking
permits only this documentation delta and verifies all code/build/test inputs.
No raw media, personal data, credentials or binaries enter Git. Original full-run
`evidence.json` retains `passed=false`; follow-up logs remain separate.

| Evidence under frozen root | SHA256 |
| --- | --- |
| `source-identities.json` | `30fa364825127e520b947ee488d7298483c15b686f412ffd2259e14348622669` |
| `sdk.log` | `372d2705358888a687e1a8cdcbcf02c85a7d3cff9f6d1cd57f6e16259abe5688` |
| `build.log` | `448106479f08252b58db54ceeec1e6d10d53c448653765aa3a10c787ca9054f6` |
| `tests.log` | `68415865cdd280cf5757168a2f22199f2eabb828dc54bb416b2a4c3ca85a6844` |
| `evidence.json` | `1fedec335bf3d67a4c59646bf2315969c25a8852e7f38ad5bb7e4d985f71122a` |
| `query-recheck-1.log` | `e9ccfebbdc08cf3382408034341160f206f41194b77dc44b82567658d3ffdc70` |
| `api-recheck.log` | `92e6cf7ed7ed68ef5c069c6b29beda2f51c306b0240c87c4aec10bae6942a91c` |
| `bridge-probe.log` | `6dd1ba43f9b598e7156d66a00d6c3ef6cc0054b3555afb0b24f2aaf2c1cee9eb` |
| `paired-recheck.log` | `1221814fea9d06e6579a9e880dafd54348557c296e125eebd5c5b591fdaa4f78` |
| `source/src/Cmsify.Core/bin/Release/net10.0/Cmsify.Core.dll` | `339581bf7cd998d454f63f5ad8db543cba22c0737010758094520dd3db66c7e8` |
| `source/src/Cmsify.Infrastructure/bin/Release/net10.0/Cmsify.Infrastructure.dll` | `ea09e9be5a926c3b6d91ab46d179f7cd732270e5fef432370c1b714a1d11d649` |

## PR handoff

Owner requested commit, push and PR on2026-10-23. Fresh pre-commit checks pass:
7 core tests (0.725s),38 PostgreSQL/infrastructure/registration/PNG checks (15.615s),
and `git diff --check`. Current main and branch base are both
`4ccabdadee4397cdced461e29e9f3dac79783597`; reviewed code/build/test inputs still
match the frozen qualification, with only this final evidence-document delta.
Changelog and integration guide are included. No further feature work is required
for PR review; stable full-suite CI qualification remains an explicit gate before
release. Package publication/tagging and host integration are separate follow-ups.
