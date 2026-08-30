# Release smoke harness tests

Run the deterministic unit suite from the repository root:

```powershell
node --test tests/release-smoke/*.test.mjs
```

The tests inject process, Docker, HTTP, clock, retry, cleanup-registration, and evidence-writing boundaries. They do not require Docker. They enforce the exact certification scenario order, validation before resource creation, bounded retries and logs, unchanged candidate image identity across restart, backup before destructive state loss, restoration into fresh volumes, cleanup at every failure boundary, and credential-free evidence.

An actual rehearsal requires both exact candidate images to be imported first. Release OCI layouts are imported through `scripts/release/load-oci-candidate.mjs` with their descriptor-bound `release-manifest.json`; they must not be passed to native `docker load`. The loader exposes the canonical tags only after a digest-preserving isolated-registry transfer and exact RepoDigest verification. The harness itself never builds or pulls either candidate:

```powershell
node eng/release-smoke/cli.mjs certify `
  --api-image cmsify-task12-api:local `
  --admin-image cmsify-task12-admin:local `
  --version 0.0.0-local `
  --source-sha (git rev-parse HEAD) `
  --output artifacts/release-smoke/local
```

The run creates only resources bearing its generated `cmsify-smoke-*` scope and exact ownership labels. On failure it prints bounded, redacted container-log tails, writes `evidence.json` using schema `cmsify.release-smoke.v1`, and removes only resources whose names and labels match that validated scope. Dependency images are immutable digest references; the candidate `docker run` commands use `--pull never` and exact inspected image IDs.

Do not treat a failed or unperformed rehearsal as certification. In particular, the local Admin candidate remains unavailable until the exact `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` bytes are publicly consumable or a separately approved stable replacement is pinned.
