# Explicit Display P3 PNG policy - 2026-10-27

Base `05bc81e` (v0.8.22 release documentation), initially clean `main`; work is on
`feature/embedded-png-cicp-sdr` in the existing checkout. Owner authorized related
local Cmsify changes, explicitly excluding release/commit/push/PR. No worktree,
package publication, schema migration, live content or retention change.

Plan: add test-first exact policy/metadata/variant admission; add optional policy
configuration/snapshot/readiness and bounded queue negotiation; bind version4
manifest metadata/reads while leaving versions1/2/3 unchanged; qualify synthetic
PostgreSQL orchestration, regressions and one final independent review coordinated
with PuppiesPlus's native decoder foundation. Raw WordPress sources stay outside
this repository. Native decoding/color fidelity is the host's separate evidence.

Contracts: `PngCicpSdrPolicyId=png-cicp-sdr-webp-v1`, conversion
`png-cicp-p3-q8-srgb-strip-v1`. `PngCicpSdrEnabled=false` by default. Exact PNG8-bit
sRGB selected metadata, original8/16 and four320/640/1280/4096-bound variants at85
quality;30MP/8192edge. No SDR-base flag. Existing processor capability interface,
application handlers/DTOs, repository transactions/receipts, JSON transport and
private/consumer read authority remain the owners. No policy fallback or migration
write bypass. Manifest v4 includes source/selected conversion metadata and variants;
previous hash recipes and retained immutable objects are unchanged.

Commands from this checkout (all `rtk` / `rtk proxy`):

```powershell
rtk proxy dotnet test --project tests/Cmsify.Core.Tests --no-restore --filter-class '*EmbeddedPngCicpPolicyTests'
rtk proxy dotnet test --project tests/Cmsify.Infrastructure.Tests --no-restore --filter-class '*EmbeddedPngCicpProcessingTests' --max-threads 1
rtk proxy dotnet test --project tests/Cmsify.Infrastructure.Tests --no-restore --filter-class '*EmbeddedPhotoRegistrationTests' --filter-method '*PngCicp*' --max-threads 1
rtk proxy dotnet test --project tests/Cmsify.Infrastructure.Tests --no-restore --filter-class '*EmbeddedPngCicpProcessingTests' --filter-class '*EmbeddedPhotoRegistrationTests' --max-threads 1
rtk proxy dotnet test --project tests/Cmsify.Core.Tests --no-restore --max-threads 1
rtk proxy dotnet test --solution Cmsify.slnx --no-restore --max-threads 1
```

RED: Core7 cases have1 expected positive-proof failure; PostgreSQL6 new scenarios
fail at policy enrollment/revision; both new snapshot/readiness cases fail (the
uncopied flag is false and readiness incorrectly reports Ready). GREEN:27 focused
PostgreSQL/registration cases pass,0 fail/skip,14.949s; full Core289 pass,0 fail/skip.
Uses normal Testcontainers PostgreSQL17-alpine, one container per process and
isolated databases per test. Synthetic three-byte originals/incomplete WebP headers
exercise orchestration, not actual compressed pixels. Covers disabled opt-in,
fresh-context replay, legacy/new retained manifests, valid-but-tampered metadata,
throwing/missing optional adapter isolation, wrong SHA/workspace/replay intent and
original retention. No fresh-process kill/resume or deployed support claim.

Full solution log retained at
`artifacts/qualification/png-cicp-sdr-20261027/solution-tests.log`:2,127 pass,
0 failed/skipped,463.609s. Infrastructure463.405s/API447.538s; all six test projects
complete green. Source/build/report hashes are pinned in the coordinated
PuppiesPlus `docs/execution/P08-display-p3-sdr-policy-evidence.json`. The actual
WordPress3-scan/12-WebP evidence is native-supervisor-only in that checkout,
not a combined source-build persistence fixture or Cmsify native support claim.
Testcontainers removes its PostgreSQL/Ryuk containers at process exit; final
Docker inventory shows only unrelated `cmsify-postgres-1`, left untouched.

SDK10.0.401 is the only installed SDK. `global.json` requests10.0.400 with
`latestPatch`, selecting401. AGENTS names400; this evidence explicitly does not
claim an exact400 run. Docker29.8.2, existing default local Docker Engine; no remote
endpoint. Source/build hashes are in the coordinated evidence; no package artifact
or deployment identity is claimed by these source builds.

Ruling: use installed patch401 selected by the repository's own global.json and
record the discrepancy with AGENTS, rather than install/change SDKs or claim400.
Cost: no independent exact400 verification. No user content, deployment or package
version is changed by this choice.

## Final review fix

Coordinated read-only review finds one Important issue: optional adapter
self-cancellation escapes capability lookup and can block eligible legacy jobs.
Add an adapter TaskCanceledException case with an active worker token and a real
caller-cancellation case retaining queued/unclaimed work. RED four cases: one
self-cancel failure. Catch filtering now propagates only actual worker-token
cancellation; other optional adapter failures produce unavailable capability.
Focused PostgreSQL/registration29 pass,0 failed/skipped,17.062s. One covered fix
pass; no second review. Final full Release verification passes2,129 tests,
0 failed/skipped,452.444s; log:
`artifacts/qualification/png-cicp-sdr-20261027/solution-tests-after-review.log`.
Release build after the fix passes with0 warnings/errors. Earlier2,127 full
Debug result remains pre-review evidence, with its reviewed source hashes kept
separate from the final code and Release build identities.

The review has no Critical/Minor findings. It checked53 hash joins before the
covered fix; it did not rerun tests or independently read source originals
(permission denied). Ruling: actual-source persistence/Owner UI/associations/
publication, source-built deployment/process recovery, full PNG/fuzz/maximum-size/
cross-platform fidelity, clinical/perceptual/HDR fidelity, exact SDK400, earlier
migration decisions and release readiness remain outside this review acceptance.
Cost if overstated: false source/fidelity/launch readiness; all such gates stay
open. No deferred Minor; no new commit/push/PR/release. The changelog is updated.
