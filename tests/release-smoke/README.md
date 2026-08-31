# Release smoke harness tests

Run the deterministic unit suite from the repository root:

```powershell
node --test tests/release-smoke/*.test.mjs
```

The tests inject process, Docker, HTTP, clock, retry, cleanup-registration, and evidence-writing boundaries. They do not require Docker. They enforce the exact certification scenario order, validation before resource creation, bounded retries and logs, unchanged candidate image identity across restart, backup before destructive state loss, restoration into fresh volumes, cleanup at every failure boundary, and credential-free evidence.

An actual rehearsal requires both exact candidate images to be imported first. The original OCI archive is the certified release artifact. Release OCI layouts are imported through `scripts/release/load-oci-candidate.mjs` with their descriptor-bound `release-manifest.json`; they must not be passed directly to native `docker load`. Pinned Skopeo converts each verified candidate offline into run-owned Docker transport scratch with no network or Docker socket access and a read-only source mount. Docker loads only that transport scratch; the loader verifies the config digest, ordered DiffIDs, platform, all required OCI labels, and the canonical tag before exposing it to this harness. The scratch is deleted on success and failure and is never checksummed, uploaded, or promoted. The harness itself never builds or pulls either candidate:

```powershell
node eng/release-smoke/cli.mjs certify `
  --api-image cmsify-task12-api:local `
  --admin-image cmsify-task12-admin:local `
  --api-manifest-digest <api-manifest-sha256> `
  --admin-manifest-digest <admin-manifest-sha256> `
  --version 0.0.0-local `
  --source-sha (git rev-parse HEAD) `
  --output artifacts/release-smoke/local
```

The two manifest digests must be copied from the already-checksummed `release-manifest.json`. They remain the certified artifact identities in each candidate's `manifestDigest` evidence field regardless of absent, stale, unrelated, matching, or multiple Docker `RepoDigests`; Docker inspection contributes only each exact runtime `imageId`, platform, version, and revision. The run creates only resources bearing its generated `cmsify-smoke-*` scope and exact ownership labels. On failure it prints bounded, redacted container-log tails, writes `evidence.json` using schema `cmsify.release-smoke.v1`, and removes only resources whose names and labels match that validated scope. Dependency images are immutable digest references; the candidate `docker run` commands use `--pull never` and exact inspected image IDs.

Do not treat a failed or unperformed rehearsal as certification. In particular, the local Admin candidate remains unavailable until the exact `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` bytes are publicly consumable or a separately approved stable replacement is pinned.
