# Durable Media Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Every behavioral change follows strict red-green-refactor TDD and every task receives an independent spec-and-quality review.

**Goal:** Close Task 8 / F-09 by making media database and blob state recoverable, retryable, and reconcilable across local and S3-compatible storage, process restarts, and multiple API replicas.

**Architecture:** `SyntaxCircus.Storage` owns reusable keyed object storage, metadata, bounded listing, disposable reads, and local/S3 provider mechanics. Cmsify owns deterministic key naming, durable upload state, 30-day tombstones, leased deletion intents, orphan policy, authorization, metrics, and operator recovery.

**Specs:** `docs/v1-release-readiness.md` F-09, `docs/project plan/09_media_api.md`, `docs/superpowers/plans/2026-08-24-v1-remediation.md` Task 8, and the approved 2026-08-26 planning decisions in this thread.

## Global Constraints

- Do not redo or alter the completed intent of Tasks 1–7.
- Every production behavior starts with a focused failing test whose failure is observed and recorded.
- Keep Testcontainers, builds, and other heavy checks serial.
- Never hand-edit generated TypeScript output.
- `SyntaxCircus.Storage` changes are independently reusable, committed in its repository, packed, and ultimately consumed by exact released version; do not source-copy its implementation into Cmsify.
- Do not push, merge, tag, publish, or release either repository without explicit user approval.
- Cmsify keeps its existing public media request/response shapes and adds no restore endpoint.
- Soft-deleted blobs remain recoverable for 30 days. Existing soft-deleted rows receive a fresh 30-day window at migration time.
- Orphans under Cmsify-managed prefixes are automatically deleted only after a 24-hour grace period.
- New keys are `cmsify/media/{workspaceId}/{yyyy}/{MM}/{assetId}_{safeFileName}`. Existing `default/...` keys remain readable and are reconciled as a legacy managed prefix.
- Reconciliation defaults: 300-second interval, 300-second lease, batch size 100, 30-second retry base, and 3,600-second retry cap.
- Metrics use only bounded provider/reason/outcome values; never label metrics or logs with asset IDs, workspace IDs, storage keys, file names, or exception messages.

### Task 1: Extend shared storage contracts and local provider

**Repository:** `E:\dev\SyntaxCircus\SyntaxCircus.Storage`

**Produces:** Provider-neutral metadata and stable bounded listing while preserving existing store/read/exists/delete/access-URL behavior and disposal.

- [ ] Add failing contract/local tests for metadata of existing and missing keys; lexicographically ordered prefix listing; `afterKey` continuation; page-size bounds 1–1,000; empty pages; traversal/root containment; cancellation; and `StorageReadResult` disposal.
- [ ] Add `StorageObjectMetadata(Key, SizeBytes, ContentType, LastModified)`, `ListStorageObjectsRequest(Prefix, AfterKey = null, PageSize = 100)`, and `StorageObjectPage(Items, NextAfterKey)`.
- [ ] Add default `IStorageProvider.GetMetadataAsync` and `ListAsync` members that throw `NotSupportedException` for third-party implementations until adopted.
- [ ] Implement metadata and listing in `LocalFileStorageProvider`; keys are normalized to `/`, results are ordinally sorted, `NextAfterKey` is the last returned key only when another matching object exists, and all resolved paths remain under `RootPath`.
- [ ] Update package README and run the focused tests followed by the full sibling Release suite.
- [ ] Commit and self-review in the sibling repository.

### Task 2: Add the shared S3-compatible provider

**Repository:** `E:\dev\SyntaxCircus\SyntaxCircus.Storage`

**Consumes:** Task 1 contracts.

**Produces:** A reusable S3/MinIO provider with complete resource ownership and configuration-driven selection.

- [ ] Add failing MinIO Testcontainers tests for keyed store/read, content type and size metadata, ordered prefix/after-key paging, missing reads/metadata, idempotent delete, cancellation, response-stream disposal, and owned versus injected client disposal.
- [ ] Add validated `S3StorageOptions` under `Storage:S3` for bucket, region, service URL, access key, secret key, and path-style behavior.
- [ ] Implement `S3StorageProvider` with caller-supplied keys, `ListObjectsV2` bounded paging using `StartAfter`, `HeadObject` metadata reads, nullable missing reads, idempotent missing delete, and disposal of every AWS response/client it owns.
- [ ] Extend `AddStorageProvider` to select local or S3 case-insensitively while preserving the existing local default.
- [ ] Update package documentation, run the focused MinIO tests serially, then run the full sibling Release build/test/pack.
- [ ] Commit and self-review in the sibling repository. Stop before push, merge, tag, or publication; record the locally packed exact version for Cmsify development.

### Task 3: Add Cmsify media lifecycle persistence and workers

**Repository:** Cmsify.

**Consumes:** The exact locally packed Task 2 package during development and the exact released package before final certification.

**Produces:** Database-first uploads, durable deletion intents, leased retries, missing-blob detection, and resumable orphan reconciliation.

- [ ] Add failing Core/Infrastructure/PostgreSQL tests for every state transition, invalid transition, deterministic key, abandoned upload, failed deletion, retention boundary, exponential retry cap, expired-lease reclaim, two-worker race, missing/reappearing blobs, orphan grace, managed-prefix restriction, and persisted scan progress.
- [ ] Add `MediaBlobState` values `PendingUpload`, `Available`, `UploadFailed`, `DeletePending`, `Deleted`, and `Missing`; add state/timestamp fields to `MediaAsset`.
- [ ] Add a durable deletion-intent entity with optional asset ID, provider, key, bounded reason, not-before/next-attempt timestamps, attempt count, bounded last error, completion time, and lease owner/token/expiry. Enforce one live intent per provider/key.
- [ ] Add a reconciliation-checkpoint entity per provider/prefix with after-key progress and a fenced lease.
- [ ] Add the EF migration and snapshot. Existing active rows become `Available`; existing soft-deleted rows become `DeletePending` with deletion intents due 30 days after migration execution.
- [ ] Implement repository operations with short transactions and `FOR UPDATE SKIP LOCKED`. A claim may complete only with its current unexpired owner/token; expired work is reclaimable.
- [ ] Implement a hosted reconciliation service. Each cycle claims due deletion intents, converts stale pending uploads to `UploadFailed` plus immediate cleanup, verifies a bounded asset batch through metadata, and scans one bounded page per managed prefix for orphans.
- [ ] Register validated `MediaOperationalOptions` with the exact defaults in Global Constraints and bounded exponential backoff.
- [ ] Add bounded counters/gauges for pending deletion, stale upload, missing blob, scan count, orphan discovery, claim/reclaim, deletion outcome, retry, and cycle failure.
- [ ] Run focused Core and Infrastructure tests serially, including PostgreSQL and MinIO, then commit and self-review.

### Task 4: Integrate the lifecycle with the media API

**Repository:** Cmsify.

**Consumes:** Task 3 persistence and worker interfaces.

**Produces:** Observable upload/read/delete behavior that never exposes incomplete assets and preserves authorization and ETag contracts.

- [ ] Add failing API integration tests for database failure before storage, storage failure/partial write, database failure after storage, successful upload, hidden pending/failed/missing/deleted assets, missing blob ProblemDetails, disposal after streamed responses, 30-day tombstone creation, reference conflict, ETag mismatch, Reader/Editor boundaries, workspace isolation, restart cleanup, and two replica workers.
- [ ] Persist a `PendingUpload` row with its final asset ID/key before calling storage; storage receives that exact key. Mark it `Available` only after successful storage and metadata capture.
- [ ] On a failed or canceled upload, best-effort mark `UploadFailed` and enqueue immediate idempotent cleanup; if that write cannot complete, stale-upload reconciliation remains authoritative.
- [ ] Return only `Available` assets from list/get/file. A missing stored object returns RFC 7807 `404` type `media-blob-missing` without provider/key leakage.
- [ ] Register `StorageReadResult` with response disposal before returning its stream.
- [ ] Soft delete, `DeletePending`, and the 30-day deletion intent are saved atomically after reference and `If-Match` checks.
- [ ] Preserve existing public media contracts/OpenAPI shapes and run API integration tests serially.
- [ ] Commit and self-review.

### Task 5: Operational documentation and Task 8 certification

**Repository:** Cmsify, with read-only evidence from the sibling package.

**Produces:** Operator-facing configuration/recovery guidance and complete F-09 evidence.

- [ ] Update `.env.example`, README configuration tables, Compose examples, the media project plan, and operations documentation with retention, retry, reconciliation, managed-prefix, alerting, and capacity behavior.
- [ ] Document operator recovery before purge: pause reconcilers, verify the blob exists, lock the asset/deletion intent in one transaction, cancel the intent, clear the soft-delete fields, restore `Available`, restart, and verify through the authenticated file endpoint.
- [ ] Document upgrade behavior for existing active/deleted media and storage-provider mismatch handling.
- [ ] Run sibling Release build/test/pack; Cmsify Core, Infrastructure, API integration, .NET client, TypeScript generation/typecheck/test/build; then full `dotnet build` and `dotnet test` Release commands, all serially.
- [ ] Update the readiness evidence/handoff with exact command results and any Docker limitation.
- [ ] Independently review the complete Task 8 diff in both repositories and resolve all Critical/Important findings before marking F-09 complete.

## Task 8 Completion Gate

- [ ] Failed uploads cannot create permanent untracked blobs.
- [ ] User deletion creates a durable recoverable tombstone and cannot lose deletion work across crashes or replicas.
- [ ] Reconciliation detects database-to-blob and blob-to-database divergence without touching foreign prefixes or young uploads.
- [ ] Local and MinIO behavior, authorization, metadata, disposal, metrics, migration, and operator recovery are covered by observed red-green tests.
- [ ] Cmsify consumes the exact released `SyntaxCircus.Storage` version before final release certification.
- [ ] No external push, merge, tag, publication, or release occurs without explicit approval.
