# Versioned PNG embedded processing — 2026-10-22

Local branch `feature/png-normalization-policy`, clean base `d235a53`.
This is an unreleased foundation for the authorized PuppiesPlus migration work.
The Owner subsequently authorized committing, pushing and opening a PR on
2026-10-22. The PR includes a changelog; no tag/package release, HTTP API change
or deployment is part of this handoff.

## Contract and ownership

`EmbeddedPhotoOptions.PngNormalizationEnabled` is false by default. It explicitly
opts into `png-sdr-webp-v1` alongside the existing configured policy. Candidate
validation admits PNG only, selected eight-bit sRGB, original depth8/16,
30 MP/8192 edge and four320/640/1280/4096-bound variants. The native host supplies
and qualifies decoding, animation/HDR rejection and conversion/resource isolation.
Legacy IDs keep24 MP/eight-bit/three variants and version1 hashes.

The additive policy capability overload defaults to the old lookup. Existing
single-policy hosts cannot accidentally advertise PNG support. Normal upload,
enrollment, new-revision, retry, worker, manifest/byte reads and reference handlers
keep their authority/receipt/concurrency boundaries. Infrastructure still owns
revision/job/receipt and manifest/variant transactions. Queue selection is bounded
across eligible policies, retaining one global processing slot. Missing optional
PNG support does not strand legacy work; disabled PNG is never looked up.

Originals and source dimensions remain unchanged. Additive per-manifest JSON
records `png-q8-srgb-strip-v1` and original depth; selected decode depth remains8.
Version2 PNG hashes bind provenance as well as descriptors. Tampered metadata
cannot be read or protected under the original identity. Reads/protection resolve
the persisted policy even after the opt-in is turned off. Existing immutable
original metadata is retained and checked across revisions. No SQL migration is
needed. Retention and private/reference authority remain mandatory.

## Qualification ledger

- New Core contract first failed compilation because the profile API was absent,
  then passed. Seven new PostgreSQL scenarios first failed at policy admission,
  then passed after coordinated worker/repository changes. A provenance tamper
  case first returned a manifest incorrectly and passes after integrity checks.
  A disabled-adapter probe first threw during queue lookup, then passed after
  gating the optional lookup. These are synthetic container-header fixtures,
  not native pixel or production support claims.
- Final focused PNG PostgreSQL suite:11 passed,0 failed/skipped. Covers old→new
  retained reads, immutable replay after context recreation, stale expected
  revision, cross-workspace/denied private preview, four-variant references,
  provenance tampering, invalid metadata/depth/variants, rejected→new revision,
  mixed queue/outage, PNG16 rotation and canceled-worker lease recovery.
- Public locked restore and full Release solution build passed with0 warnings/
  errors. Full six-project checkpoint passed2092 tests,0 failures/skips, in
  10m22.874s (including real PostgreSQL/MinIO/Redis dependencies). This checkpoint
  predates the final optional-lookup guard and its three focused regression cases;
  the complete final Infrastructure rerun passes851/0 failed/skipped in2m40.236s.
  No later implementation changes. The final focused PNG cases are included.
- PR handoff reruns the full six-project Release solution suite on the final
  staged implementation:2095 passed,0 failed/skipped,9m48.284s. This includes the
  final guard and all added cases; only this documentation result was added after
  that run. SDK10.0.401 and the exact explicit solution command below were used.
- First positional `dotnet test Cmsify.slnx --no-build ... -m:1` ran zero tests,
  exit5. Correct invocation uses explicit `--solution` and
  `--max-parallel-test-modules 1`; zero tests were never counted as a pass.
  A test initially attempted an inaccessible internal lease API; it was corrected
  to cancel the public worker, leaving the real persisted lease for recovery.
- Actual SDK is10.0.401, allowed by `global.json` latestPatch but different from
  AGENTS' stated10.0.400. No claim of an exact10.0.400 run. All commands prefix
  `rtk proxy`; Docker Desktop local engine, no remote production services used.

Commands: `dotnet restore Cmsify.slnx --locked-mode --verbosity quiet`;
`dotnet build Cmsify.slnx -c Release --no-restore --no-incremental --verbosity quiet -m:1`;
`dotnet test --solution Cmsify.slnx -c Release --no-restore --verbosity quiet --max-parallel-test-modules 1`;
focused `dotnet test tests/Cmsify.Infrastructure.Tests -c Release --no-restore --filter FullyQualifiedName~EmbeddedPngProcessingTests --verbosity quiet`;
final full `dotnet test tests/Cmsify.Infrastructure.Tests -c Release --no-restore --verbosity quiet`.

Fresh-context whole-increment static review found no material defects; suggested
regressions above were added. Native Windows/Linux actual-source evidence lives
in the paired PuppiesPlus `docs/execution/P08-png-policy-implementation.md` and
machine evidence; no customer exports, credentials or binary media enter Git.
Host stays on published0.8.20: persisted Owner editing/preview/publication/browser
adoption of this profile must follow a separately authorized upstream release.
