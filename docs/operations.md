# Operating Cmsify

## Configuration

Cmsify reads `appsettings.json`, environment variables, and development dotenv files. Copy `.env.example` for local development. Repository-level `.env` values load before app-level files such as `src/Cmsify.Api/.env.local`, so the closer app-level file wins. See the root [configuration inventory](../README.md#configuration) for every supported API and Admin setting, its default, and its production guidance.

Use environment-specific secret management in production. Do not commit credentials, connection strings containing passwords, encryption keys, or bearer tokens.

## Container deployment

The repository's [`docker-compose.prod.yml`](../docker-compose.prod.yml) is a complete single-host deployment template with PostgreSQL and named volumes for database data, local media, and Admin Data Protection keys. A reviewed SemVer tag promotes matching versioned `syntaxcircus/cmsify-api` and `syntaxcircus/cmsify-admin` images from one immutable commit; branch builds never publish. The template keeps the versioned image pair and their exact literal manifest digests together; use `CMSIFY_IMAGE_PREFIX` to pull from a private registry/repository instead.

```powershell
# Build both local images (including amd64 and arm64 tags).
./Build-CmsifyDocker.ps1 -ImageTag local

# Authenticate separately, then publish a multi-architecture test build.
docker login registry.example.internal
./Build-CmsifyDocker.ps1 -ImageTag 0.1.0-test -Registry registry.example.internal/cmsify -Push
```

Copy [`docker-compose.prod.env.example`](../docker-compose.prod.env.example) to a private environment file, set `CMSIFY_VERSION` to an exact Docker Hub release tag (or `local` for locally built images), update the literal matching 64-character API/Admin manifest digests in `docker-compose.prod.yml`, set `CMSIFY_IMAGE_PREFIX` to the registry prefix when applicable, replace all placeholder secrets, then run Compose with `--env-file`. The script requires `-Registry` whenever `-Push` is used, and it never accepts registry credentials; authenticate with `docker login` first.

Bind the API and Admin ports to loopback and terminate TLS at a host reverse proxy. If ports are published publicly instead, secure them with TLS and configure the proxy/trusted-forwarded-header settings for that deployment.

## Persistence and upgrades

The API runs EF Core migrations at startup. Back up PostgreSQL and the configured media storage before upgrades. Treat migrations and media as a matched deployment: restoring only the database or only the media volume can leave content references unusable.

The development Compose file persists PostgreSQL under `local/postgres` and local media under `local/media`. The production Compose file uses named volumes (`postgres_data`, `media_data`, and `admin_data_protection_keys`). Choose an external PostgreSQL and object-storage backup policy for production rather than relying on container lifecycle.

### Backup, restore, and upgrade checklist

Use this checklist before each production upgrade:

1. Confirm `/health/ready` is healthy and identify the current image tag.
2. Back up PostgreSQL and media together. For the supplied Compose deployment, back up the `postgres_data` and `media_data` volumes; for S3, back up the bucket or confirm its versioning and recovery policy.
3. Record the Admin Data Protection key volume (`admin_data_protection_keys`) as part of the deployment backup. Losing it signs out existing Admin sessions.
4. Update `CMSIFY_VERSION` and the matching literal API/Admin manifest digests in `docker-compose.prod.yml`, then run `docker compose --env-file .env.prod -f docker-compose.prod.yml pull` when using a registry image, followed by `up -d`.
5. Verify `/health/live`, `/health/ready`, Admin sign-in, and a representative media download.

To restore, stop application traffic, restore the database and its matching media snapshot, restore Data Protection keys when retaining sessions matters, then start the stack and wait for readiness. Do not restore only one of the database and media snapshots: content can otherwise point at missing or mismatched files. If the upgraded stack does not become ready, return to the prior image tag and restore the matched backup before accepting traffic.

### v0.1.x to v1 upgrade and rollback

The supported v1 path starts from the baseline recorded in the [upgrade fixture manifest](../tests/upgrade/fixtures/v0.1.3/manifest.json). The fixture [checksum inventory](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS), [harness runbook](../tests/upgrade/README.md), and [upgrade workflow](../.github/workflows/upgrade-rollback.yml) define the repeatable certification contract. An installation older than the recorded baseline is not a direct v1 upgrade source.

Prepare and deploy in this order:

1. Identify the deployed API image by immutable digest and confirm `/health/ready`. Verify the checked-in fixture and its exact baseline API, PostgreSQL, and MinIO digests with `node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3`. Release certification must also run `verify-release-baseline` so a newly published stable `0.1.x` cannot leave the fixture stale.
2. Quiesce writes long enough to take one matched PostgreSQL/media backup generation. Record its creation time, source image digest, database checksum, every media object/key checksum, and any storage snapshot/version identifiers in one manifest. Verify every checksum before resuming writes or continuing. Back up Admin Data Protection keys separately when retaining Admin sessions matters; they are not a substitute for either matched data member.
3. Build or load the exact candidate once, record its immutable image ID/digest and source SHA, and run the [full rehearsal](../tests/upgrade/README.md#build-and-rehearse-an-exact-candidate) from the verified fixture. Require all eleven phases, both clean passes, matched-backup rollback, exact media validation, and an empty ownership-label cleanup audit.
4. Deploy that exact rehearsed candidate while retaining the verified matched backup and exact prior image digest. Do not overwrite, age out, or detach either backup member during the rollback window. Validate `/health/live`, `/health/ready`, Admin sign-in, representative authenticated content reads, and byte-for-byte representative media downloads before restoring traffic.

When a rehearsal fails, inspect its run-owned `artifacts/upgrade-tests/<run-id>/report.json`. The bounded atomic `cmsify.upgrade-diagnostics.v1` report records the fixture/baseline source and exact candidate identity, the first `failedStage` with allow-listed `failureEvidence`, and the separate `cleanup` outcome. A successful cleanup never replaces rollback failure evidence. If the report reaches its size bound, the harness deterministically replaces lower-priority successful-phase evidence with `truncated: true` plus safe readiness/assertion counts; it always retains the required source/candidate identity, first failed stage, failure evidence, and cleanup outcome. The report excludes raw exceptions, environment values, request headers, connection strings, tokens, and secrets.

If the v1 deployment fails, do not merely change the image tag against the upgraded volumes:

1. Stop traffic and all API/Admin/worker processes. Preserve candidate diagnostics and record the failure time, candidate identity, migration/readiness state, and correlation IDs without copying secrets or response bodies.
2. Re-verify the retained matched-backup manifest and both backup members. If verification fails, rollback is unproved; do not destroy the only remaining state while investigating.
3. Discard the database and media state written by v1. Create clean database storage and clean media storage; restoring over candidate-written state is unsupported.
4. Restore PostgreSQL and media from the same pre-upgrade backup generation. Never mix a database dump with an older/newer media snapshot.
5. Start the exact prior image by immutable digest. **Never run `0.1.3` (or another pre-v1 binary) against v1-written database or media state.**
6. Before accepting traffic, require `/health/ready`, authenticated representative content and version reads, expected authorization behavior, and exact representative media bytes. Confirm candidate-only canary/writes are absent when the incident procedure created them.

For installations older than the manifest's `0.1.3` baseline, first follow the historical supported path to exactly `0.1.3`. Verify live/readiness health, sign-in, representative content, and media. Then quiesce writes, create and checksum a new matched PostgreSQL/media backup at `0.1.3`, and only then begin the v1 sequence above. When the moving fixture advances to a later published stable `0.1.x`, that newly recorded version replaces `0.1.3` in this rule.

Treat each rehearsal report as evidence only for its exact candidate image ID and source SHA. A successful harness implementation or an older run does not certify a rebuilt image, a different architecture, a changed source revision, or a production backup procedure that was not itself verified.

## Network and TLS

Place the API and admin UI behind a trusted reverse proxy that terminates TLS and forwards the required host/proto headers. Set `Admin__ApiBaseUrl` to the API address reachable by the admin service. Allow only known frontend origins through `Cors__AllowedOrigins`.

API clients use `Authorization: Bearer cmsify_...`. Keep those tokens server-side, assign an expiry where possible, and rotate or revoke them when a service changes ownership. A rotated token is returned once and cannot be recovered later.

## Health and diagnostics

- `GET /health/live` confirms that the process is running.
- `GET /health/ready` confirms that the database and storage dependencies are reachable. It returns machine-readable JSON with the overall status, per-check status/duration/description, and `metadata` containing the deployed application version and report generation time.
- `GET /health/dashboard` is an optional, self-contained HTML view of the same readiness checks for operators. Enable it with `Api__HealthDashboardEnabled=true` (or `Api:HealthDashboardEnabled=true` in configuration). It is disabled by default and should be exposed only through an internal reverse-proxy allow-list: the page can reveal dependency failure details and is not a public status page.
- Swagger/OpenAPI is served at `/swagger` when enabled.
- Responses include RFC 7807 ProblemDetails for failures. The API and SDK use correlation IDs; retain the `X-Correlation-Id` and `traceId` values when escalating an error.

Use `/health/live` and `/health/ready` for container orchestration and monitoring. A failed readiness check should remove the instance from traffic while leaving the process available for diagnosis. Use the dashboard only for a human operator investigating a deployment or incident; it always renders the current report and is not a probe target.

## Durable webhooks and scheduled publication

Webhook-producing mutations write a durable outbox record in the same database transaction. Delivery is **at least once**: consumers must deduplicate attempts by `X-Cmsify-Event-Id`, which remains stable across retries. Cmsify signs the exact transmitted JSON bytes with the endpoint secret in `X-Cmsify-Signature`; validate the HMAC against those bytes, not a reserialized payload.

Webhook egress is direct-only: there is no proxy configuration or proxy mode. Each delivery attempt resolves the endpoint host once, rejects the endpoint when resolution yields no addresses or a mixed result containing any private, loopback, or reserved address, and connects only to the approved result set while retaining the original host for HTTP and TLS. Ambient machine and environment proxy settings are bypassed. Automatic redirects are disabled, so a redirect response is a failed delivery rather than a new destination. Every retry performs a fresh resolution and validation before it connects. HTTPS is required by default; `Webhook__AllowHttp=false` must remain set, and HTTP may be enabled only for controlled development.

Workers claim bounded outbox, delivery, and scheduled-publication batches with owner/token leases. A live lease cannot be stolen; an expired lease is recovered by a new worker, so API replicas can safely process work concurrently. Delivery failures use exponential backoff. At `Webhook__MaxAttempts`, the delivery becomes a retained dead letter with its error, attempt, event, and terminal timestamp; operators can explicitly requeue terminal rows through the existing retry route. Retention deletes only old processed outbox rows and successfully delivered logs in bounded batches. Pending, retrying, and dead-letter rows are never deleted automatically.

The shared `Cmsify.Operational` meter provides low-cardinality counters and gauges without event, workspace, or endpoint labels: `cmsify.webhook.outbox.pending`, `cmsify.webhook.outbox.claimed`, `cmsify.webhook.outbox.reclaimed`, `cmsify.webhook.outbox.materialized`, `cmsify.webhook.outbox.failures`, `cmsify.webhook.delivery.due`, `cmsify.webhook.delivery.claimed`, `cmsify.webhook.delivery.reclaimed`, `cmsify.webhook.delivery.succeeded`, `cmsify.webhook.delivery.retried`, `cmsify.webhook.delivery.dead_lettered`, `cmsify.webhook.destination.rejected`, `cmsify.webhook.connection.failed`, `cmsify.schedule.due`, `cmsify.schedule.claimed`, `cmsify.schedule.reclaimed`, `cmsify.schedule.published`, `cmsify.schedule.failures`, `cmsify.cleanup.outbox_deleted`, and `cmsify.cleanup.deliveries_deleted`.

### Webhook signing-secret key rotation

New signing-secret ciphertext is `v2` and records its key ID. Configure `Secrets__ActiveKeyId` and a canonical Base64 `Secrets__EncryptionKeys__<keyId>` value that decodes to exactly 32 bytes. The active ID selects new writes; retain every previous `EncryptionKeys` entry while any stored `v2` ciphertext references it. `Secrets__EncryptionKey` is a legacy `v1` migration input only and is never eligible for new writes. Production rejects the checked-in development fixture and obvious weak key material before readiness.

For the production Compose template, copy [`docker-compose.prod.keyring.env.example`](../docker-compose.prod.keyring.env.example) to an untracked deployment-only keyring file, replace the active-key placeholder, and set `CMSIFY_API_KEYRING_ENV_FILE` in the private Compose environment file to that path. Compose supplies that keyring file only to the API service and does not wholesale-pass the main interpolation file; only explicitly listed API settings cross that boundary. Add a `Secrets__EncryptionKeys__<oldId>` line for every retained `v2` key and leave the legacy line commented unless `v1` ciphertext still exists. A missing file, placeholder, invalid Base64, wrong key length, or missing active ID/key fails API startup before readiness. Never commit the deployment keyring file.

Generate a production key with a CSPRNG, store it only in the deployment secret manager, and assign it a stable operational ID:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Use this enable-observe-disable runbook:

1. Deploy the v2-capable reader/writer first with the new active key, all retained `v2` keys, and any required legacy `v1` key. Keep `Secrets__Rotation__Enabled=false`. While disabled, Cmsify performs one non-mutating inventory preflight: it counts remaining ciphertext, publishes the complete bounded `remaining` snapshot (including zero categories), and exits without claiming or updating a row. A transient database/count failure is retried after the configured delay until one refresh succeeds; until then, an absent gauge means no successful preflight and an existing gauge is the prior snapshot, not a fresh result.
2. Confirm readiness and normal webhook delivery. Monitor decrypt failures by version/key ID, rows rotated/skipped/failed, rotation-cycle duration, and the database-derived remaining ciphertext count by version/key ID. Decrypt failures also cover normal webhook dispatch reads, not only the rotation worker.
3. Enable rotation for one bounded deployment window. Investigate every decrypt or row failure; do not remove a key while any row can reference it.
4. Wait for the remaining count to reach zero, then require zero again on an independent subsequent pass. Disable rotation after that verification.
5. Retire old keys only in a later, explicit configuration change after the independent zero result. Do not combine key retirement with a binary rollback.

Export these exact `Cmsify.Operational` instruments for the rotation window. All labels are bounded: configured key IDs remain themselves and unknown values become `unknown`.

| Instrument | Type and unit | Labels | Meaning |
| --- | --- | --- | --- |
| `cmsify.webhook.secret.decrypt_failures` | counter | `version`, `key_id`, `reason` | Failed attempts to decrypt a stored secret. Versions are `v1`, `v2`, or `unknown`; reasons are `configuration`, `unknown_version`, `unknown_key`, `malformed_ciphertext`, `authentication`, or `unknown`. |
| `cmsify.webhook.secret.rotation.rows` | counter | `outcome` | Rows handled during a cycle: `rotated`, `skipped`, or `failed`. |
| `cmsify.webhook.secret.rotation.cycles` | counter | `outcome` | Completed worker cycles: `succeeded` or `failed`. |
| `cmsify.webhook.secret.rotation.duration` | histogram, seconds (`s`) | none | Duration of each rotation cycle. |
| `cmsify.webhook.secret.rotation.remaining` | observable gauge | `version`, `key_id` | Database-derived count of ciphertext that is not active-key `v2` material. |

Treat every decrypt failure and every `failed` row as an investigation item before continuing. Rotation logs each failed row as a secret-safe structured warning with `endpoint_id`, normalized ciphertext version, configured-or-`unknown` key ID, and bounded reason; it never includes plaintext, ciphertext, URLs, or key material. Completion is not an idle worker: require the `remaining` gauge to be zero for every version/key ID, then confirm zero again on an independent later pass before disabling rotation and considering retirement.

After any `v2` write, a rollback requires a v2-capable binary and every key that can be referenced by stored ciphertext. A pre-v2 reader cannot safely decrypt the database. Keep rotation disabled while rolling back, restore a matched database backup when required, and investigate configuration errors before attempting another rotation window.

## Durable media reconciliation

Media uploads are database-first. The API commits the final asset ID and storage key in `PendingUpload` before it writes the blob, then exposes the asset only after it reaches `Available`. A failed upload becomes `UploadFailed` with an immediate deletion intent when the database is reachable; otherwise the stale-upload pass performs that transition after `Media__Operations__AbandonedUploadMinutes` (30 minutes by default). An object missing during verification becomes `Missing` and is hidden from callers; a later successful metadata check restores it to `Available`.

User deletion is recoverable for `Media__Operations__RetentionDays` (30 days by default). The API atomically marks the asset `DeletePending`, soft-deletes it, and creates a durable intent that is not eligible before the recovery deadline. Workers use owner/token/expiry fencing and PostgreSQL row locks, so a crashed replica's expired work can be reclaimed and a stale replica cannot complete it. Failed storage deletes retry exponentially from 30 seconds up to 3,600 seconds. Immediately before an orphan deletion, the worker renews its fenced claim and cancels the intent if a database owner has appeared.

Each cycle is deliberately bounded. By default, every 300 seconds each replica processes at most 100 due deletes, 100 abandoned uploads, 100 verification candidates for its configured provider, and one page of 100 objects per managed prefix. Only `cmsify/media/` and the legacy `default/` prefix pass configuration validation. Objects outside those prefixes are never listed or deleted by reconciliation, and an unowned managed object must be at least 24 hours old before it is queued. Size the interval/batch combination for the expected object count and provider request budget; sustained backlog growth means capacity must be increased or the underlying provider/database failure fixed.

Export the bounded `Cmsify.Operational` media instruments: `cmsify.media.deletion.pending`, `cmsify.media.upload.stale`, `cmsify.media.blob.missing`, `cmsify.media.scan`, `cmsify.media.orphan.discovered`, `cmsify.media.deletion.claimed`, `cmsify.media.deletion.reclaimed`, `cmsify.media.deletion.outcome`, `cmsify.media.deletion.retried`, and `cmsify.media.reconciliation.cycle_failures`. Alert on any cycle failure, sustained retries, reclaimed work, a non-draining pending-deletion gauge, or increases in missing blobs/orphans. Labels are limited to normalized provider, reason, and outcome values; logs and metrics do not include workspace IDs, asset IDs, keys, file names, or exception messages.

### Upgrade and provider mismatch behavior

The media-lifecycle migration marks existing active media `Available`. Existing soft-deleted media becomes `DeletePending` and receives a fresh full retention window beginning when the migration runs; it is not purged immediately based on its historical deletion date. The database default remains `Available` during the rolling upgrade so an older API replica cannot accidentally create `PendingUpload` rows that stale reconciliation would clean. New binaries explicitly write `PendingUpload` before storage.

Keep all API replicas on the same `Storage__Provider` during deployment. A deletion intent whose provider does not match the current replica is not sent to that replica's storage client; it is released for bounded retry with reason `provider_mismatch`. During a provider migration, keep a worker capable of the old provider running until its intents and retained assets are drained, or migrate both blobs and database provider/key values under a separately reviewed procedure. Do not point one provider at another provider's managed prefix.

For S3-compatible services, `Storage__S3__ServiceUrl` enables path-style addressing unless `Storage__S3__ForcePathStyle=false` is explicitly set. For local storage, the historical `Storage__Local__BasePath` remains authoritative when both it and `RootPath` are present.

### Recover a user-deleted asset before purge

There is intentionally no public restore endpoint. Use a maintenance window and an audited database transaction:

1. Stop or pause every media reconciler and wait for active deletion leases to expire; prevent new writes for the affected asset.
2. Verify the exact blob exists in the asset's recorded provider/key. Never restore the row against a guessed key or another backup generation.
3. Begin one database transaction and lock the soft-deleted `media_assets` row plus its incomplete `media_deletion_intents` row with `FOR UPDATE`.
4. Confirm the asset is `DeletePending`, its purge deadline has not elapsed, and no worker owns an unexpired intent lease. Mark the intent completed/canceled, clear its lease fields, clear `is_deleted`, `deleted_at`, `deleted_by_user_id`, `deletion_requested_at`, and `purge_after`, then transition the blob state to `Available` and update its verification timestamp.
5. Commit, resume reconcilers and traffic, and download the asset through the authenticated `/api/v1/workspaces/{workspaceId}/media/{assetId}/file` endpoint. If verification fails, pause again and restore the matched database/blob backup rather than manufacturing a second row.

After the retention deadline or a completed physical delete, recovery requires a matched database and storage backup. Never merely clear soft-delete fields after the blob has been purged.

For example, a healthy readiness response contains the existing dependency results plus deployment metadata:

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "database", "status": "Healthy" },
    { "name": "storage", "status": "Healthy" }
  ],
  "metadata": {
    "version": "0.1.0+build",
    "generatedAt": "2026-08-21T00:00:00+00:00"
  }
}
```

## Incident response checklist

When an integration fails, record the HTTP status, ProblemDetails `type`, `detail`, `traceId`, correlation ID, workspace, and client identity (never the token). Then check token expiry/revocation, workspace scope, role, CORS/proxy configuration, rate-limit responses, and database/storage readiness in that order. Escalate with those details plus the deployment image tag and the affected time range.
