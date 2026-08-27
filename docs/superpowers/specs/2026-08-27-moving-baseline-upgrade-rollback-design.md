# Moving-Baseline Upgrade and Rollback Design

**Date:** 2026-08-27
**Status:** Approved
**Remediation scope:** Task 9; finding F-04

## Summary

Cmsify will prove upgrades from the latest published `0.1.x` release to the v1 candidate by restoring a checked-in, sanitized PostgreSQL and media fixture into isolated containers. The fixture will be versioned, checksum-bound, and tied to immutable image and source provenance. It must first pass under the exact historical image before it is eligible for an upgrade rehearsal.

The rehearsal will start the candidate against the verified baseline state, allow the normal startup migration path to run, and validate database, API, authorization, scheduling, media, webhook, audit, and provenance invariants. Rollback will be proven by discarding the upgraded state, restoring a matched pre-upgrade database and media backup into clean volumes, and starting the exact historical image. Cmsify will not claim that an upgraded database can be downgraded in place.

A dedicated cross-platform Node CLI will own fixture verification and Docker orchestration. Unit tests, a full Docker workflow, the release contract, and the operations guide will make the evidence repeatable locally and in CI.

## Goals

- Maintain one authoritative fixture for the latest published stable `0.1.x` release.
- Prove that the fixture is readable and internally valid under its recorded historical API image.
- Prove that the current candidate migrates the fixture without losing or corrupting required domain state.
- Prove content and media remain usable through observable API behavior, not only row counts.
- Prove operational rollback using a matched PostgreSQL and media backup plus the exact prior image.
- Make fixture contents, provenance, and regeneration deterministic and reviewable.
- Prevent release promotion when upgrade evidence is stale, incomplete, or inconsistent.
- Document the supported path for installations older than the recorded baseline.

## Non-goals

- Supporting EF Core down-migrations or starting an old binary against a database already written by v1.
- Maintaining a fixture for every historical prerelease.
- Copying production or customer data into the repository.
- Testing every supported external object-storage vendor. The rehearsal uses the production storage abstraction against MinIO-compatible object storage.
- Replacing normal unit, integration, SDK, accessibility, or release-candidate validation.
- Publishing an image, package, tag, or release as part of the rehearsal.
- Providing a general database backup product or continuous disaster-recovery scheduler.

## Constraints and assumptions

- The authoritative baseline is the latest successfully published stable `0.1.x` release, currently `0.1.3`.
- The baseline API is `docker.io/syntaxcircus/cmsify-api:0.1.3`, pinned to its immutable linux/amd64 manifest digest. Its source commit is `bc652aec1acad7ef440576b5019a0fe7c72004b3`.
- PostgreSQL and object storage are a matched recovery unit. A rollback that restores only one is invalid.
- The v1 API applies migrations through its normal startup path.
- The fixture contains synthetic data only. Known test credentials, token hashes, webhook secrets, and encryption material are fixture-scoped and must not resemble deployment secrets.
- Docker is required for the full rehearsal. Fast manifest and orchestration tests do not require Docker.
- The harness must run on supported Windows development machines and Linux CI runners without maintaining separate PowerShell and Bash implementations.
- Generated TypeScript client files are outside Task 9 and will not be edited.

## Considered approaches

1. **Node orchestration over Docker and Compose — selected.** A small ESM CLI follows the repository's existing release-tooling pattern, works on Windows and Linux, handles JSON and checksums directly, and can be unit-tested without Docker. Docker remains responsible for PostgreSQL, MinIO, and API process isolation.
2. **A dedicated .NET Testcontainers project.** This would provide strong typed assertions but makes exact historical image orchestration, `pg_dump`/restore, media backup, and release-script integration more cumbersome. It would also couple release evidence to application test infrastructure unnecessarily.
3. **Compose plus PowerShell and Bash scripts.** This is initially simple but duplicates control flow, cleanup, and error handling across platforms. It is rejected because the two implementations could diverge.

The selected design keeps the CLI dependency-free where practical. It invokes checked tools with argument arrays rather than shell-composed commands and uses Compose only for declarative service topology.

## Fixture contract

The initial fixture lives under `tests/upgrade/fixtures/v0.1.3/` and contains:

- `manifest.json` — schema version, baseline version, source commit, historical API repository/tag/digest/platform, PostgreSQL image reference and digest, fixture generation metadata, required file list, and expected-data document path;
- `database.sql` — a normalized plain-text PostgreSQL dump without owners, ACLs, environment-specific paths, or nondeterministic dump headers;
- `media/` — representative binary objects stored beneath their exact deterministic storage keys;
- `expected.json` — stable identifiers, relationships, lifecycle states, timestamps, values, and SHA-256 media expectations used by baseline and candidate validation;
- `SHA256SUMS` — canonical fixture-relative SHA-256 entries for every fixture payload except the checksum file itself.

The manifest is the entry point. The CLI rejects unknown manifest schema versions, absolute or escaping paths, duplicate files, missing required files, unpinned external images, uppercase or malformed digests, inconsistent baseline tag/version pairs, and files not covered by `SHA256SUMS`. Checksums use lowercase hexadecimal and forward-slash paths sorted by ordinal byte order.

The database dump is reviewable text rather than a custom-format archive. Normalization fixes locale-sensitive and run-specific output while retaining all schema and data required by the historical image. The media directory is not wrapped in an archive, avoiding nondeterministic archive timestamps and making each object independently checksum-visible.

### Required synthetic coverage

The fixture must include stable, cross-referenced examples of:

- multiple workspaces and users;
- global and workspace-scoped permissions, including an actor who cannot access another workspace;
- templates with primitive fields and inline nested components;
- an acyclic component graph and representative component snapshot JSON;
- a choice set with at least two immutable revisions and published content retaining an older option label;
- draft and published content versions;
- content with future `PublishAt`, currently effective content, and expired content using the single effective range;
- media metadata and bytes for at least one available object plus lifecycle states needed to exercise the Task 8 migration boundary;
- webhook endpoints and durable delivery history without any live destination or deployable secret;
- audit rows linked to representative mutations;
- API client metadata sufficient to test authentication and workspace authorization;
- database migration history and application/image provenance expectations.

The expected-data document names every required scenario. A coverage validator fails if a required scenario or assertion category is removed, even when the remaining JSON is otherwise valid.

## Fixture creation and refresh

Fixture creation starts from the exact published baseline image and an empty database. The image initializes its own schema. A baseline-specific deterministic seeder then uses the published API for supported public operations and narrowly scoped SQL for historical states that cannot be created deterministically through that API, such as fixed timestamps, immutable revision history, and durable delivery records. Direct seed SQL is versioned beside the generator, runs only against the expected baseline migration set, and is never applied to a normal environment.

After seeding, the exact baseline image must pass the complete baseline assertion set. Only then may the generator export a normalized database dump and media tree, write `expected.json`, write provenance, and calculate checksums. Regenerating twice from clean volumes must produce byte-identical checked-in artifacts.

The fixture records the published image digest observed from the registry, not a locally reconstructed image. Source reconstruction is insufficient evidence because it does not prove the deployed artifact being used by operators.

When another stable `0.1.x` release has been published, the fixture must be refreshed from that release before any subsequent release tag can be promoted. The release verifier discovers the latest already-published stable `0.1.x` GitHub release, confirms its Docker Hub API digest, and requires both to equal the fixture manifest. This deliberately excludes the candidate tag currently being certified, avoiding a circular requirement to create a fixture from an artifact that has not yet been published. For example, `0.1.4` can be certified from the `0.1.3` fixture; after `0.1.4` is published, a later tag cannot promote until the fixture records published `0.1.4`.

## Components

### Fixture library

`tests/upgrade/fixtures/` owns immutable published-baseline artifacts. No runtime application project reads these files. A short fixture README documents provenance, regeneration, synthetic credentials, and review expectations.

### Upgrade-test CLI

`eng/upgrade-tests/` contains focused modules for:

- manifest parsing and semantic validation;
- checksum and deterministic-output verification;
- safe paths, run identifiers, and Docker resource names;
- child-process execution with bounded deadlines and sanitized diagnostics;
- Compose lifecycle and readiness;
- PostgreSQL and media restore/backup;
- HTTP, SQL, and byte-level assertions;
- fixture generation and refresh verification;
- phased rehearsal orchestration and cleanup.

The public commands are intentionally small:

- `verify-fixture` validates a checked-in fixture without starting Docker;
- `generate-fixture` recreates fixture outputs from the recorded published baseline;
- `rehearse` runs baseline, upgrade, and rollback validation against a supplied candidate image;
- `verify-release-baseline` compares the checked-in fixture with the latest already-published stable `0.1.x` release.

Machine-readable phase results are written to a run-owned diagnostics directory. Human-readable output names the current phase and the failed invariant without exposing fixture secrets or database rows.

### Container topology

`tests/upgrade/compose.yml` defines PostgreSQL, MinIO, the baseline API, and the candidate API. Only one API image is active against a database at a time. Each rehearsal supplies a generated Compose project name and creates isolated database, media, backup, and diagnostics resources.

External service images and the historical API are digest-pinned. The candidate is a local image reference supplied by the caller and is verified by immutable local image ID and expected OCI revision/version labels before use. Fixed host ports are avoided; readiness and API calls use discovered or run-specific bindings.

### CI and release integration

`.github/workflows/upgrade-rollback.yml` builds the current API candidate once and runs the full rehearsal. It triggers for pull requests and pushes that change migrations, persistence mappings, startup migration behavior, media storage or reconciliation, the fixture, the harness, container definitions, or release workflows. It also supports manual dispatch.

The existing release workflow consumes the same checked-in fixture and rehearsal command. Upgrade/rollback certification completes before the protected promotion job and before any irreversible registry or package publication. Existing release-contract tests assert this dependency and the moving-baseline gate structurally.

## Rehearsal data flow

1. Resolve repository-owned paths and validate the manifest, fixture coverage, checksums, provenance, image digests, and required tools before creating resources.
2. Create isolated PostgreSQL and MinIO volumes. Restore `database.sql` and media objects, then start the exact recorded `0.1.x` API image.
3. Wait for bounded readiness and validate the baseline through authenticated API behavior, direct migration-history checks, expected authorization outcomes, and byte-for-byte media retrieval. A fixture that fails here is invalid and is never used to judge the candidate.
4. Stop the baseline API and create a matched PostgreSQL dump and media backup from the verified state. Validate backup manifests and checksums before proceeding.
5. Start the candidate image against the verified baseline database and media state. The normal candidate startup path applies migrations; the harness does not invoke migration internals directly.
6. Wait for readiness, then validate migration history and every expected domain, authorization, scheduling, media, webhook, audit, and provenance invariant. Create a uniquely identified canary through the public API and read it back to prove the upgraded system is writable.
7. Stop the candidate. Verify the matched backup again, then discard the upgraded database and media volumes.
8. Restore the matched pre-upgrade database and media backup into fresh volumes. Start the exact baseline image and repeat baseline assertions. Candidate-only canary state must be absent.
9. Write a phase summary and clean up only the current run's resources.

The baseline validation and rollback validation use the same assertion definitions. This prevents rollback from passing a weaker contract than the original baseline.

## Candidate invariants

Candidate validation combines public behavior with narrowly targeted database checks:

- health and readiness succeed with expected build identity;
- the full expected EF migration set is present exactly once and no migration remains pending;
- every expected workspace, actor, role, and workspace permission relationship remains intact;
- unauthorized workspace access retains the documented `404` behavior;
- templates and component snapshots preserve field types and acyclic inline structure;
- choice revisions are immutable and historical published content retains its snapshotted label;
- draft, published, scheduled, effective, and expired content resolve according to `PublishAt` and the single effective range;
- media rows have the expected provider, deterministic key, lifecycle state, size, content type, and hash;
- available media downloads return exact fixture bytes, while non-available media remains hidden;
- webhook endpoint and delivery history survive without making external network calls;
- audit entries retain actor, workspace, action, entity, correlation, and timestamp relationships;
- fixture client authentication and least-privilege authorization still work;
- candidate identity labels and reported version bind the tested image to the supplied source revision;
- a public-API canary write/read succeeds without weakening immutable history or concurrency behavior.

Database assertions exist for invariants that cannot be proven through the historical public API, but they do not replace observable API checks for user-visible behavior.

## Failure handling and cleanup

The harness fails closed before mutation when:

- the fixture manifest or coverage contract is invalid;
- any checksum differs;
- an external image is not digest-pinned or provenance conflicts;
- Docker, Compose, or a required database/storage tool is unavailable;
- a resolved path escapes the repository-owned test or run directory;
- a candidate image lacks the expected identity labels.

Every execution phase has a bounded readiness or command deadline and a distinct failure code. Baseline validation, backup creation, candidate migration, candidate invariants, media verification, and rollback verification are mandatory. A skipped, timed-out, cancelled, or partially completed phase cannot result in success.

Cleanup runs from a `finally` path. It may stop or remove only containers, networks, and volumes matching both the generated run identifier and the exact harness labels. It never invokes a global prune and never removes an unlabelled or differently labelled resource. All destructive filesystem targets are resolved and proven to remain beneath the run-owned diagnostics directory.

On failure, CI captures sanitized container logs, phase results, readiness observations, migration state, assertion summaries, and manifest metadata before cleanup. It does not print secrets, token material, full database contents, environment dumps, or binary fixture payloads.

The matched rollback backup is checksum-verified before upgraded volumes are destroyed. A backup-integrity failure stops the rehearsal and reports rollback as unproven.

## Testing strategy

### Fast tests

Node tests cover:

- manifest schema and negative validation cases;
- stable SemVer ordering and latest-published-baseline selection;
- digest, platform, tag, and source-provenance consistency;
- canonical checksum parsing, calculation, missing/extra file detection, and deterministic ordering;
- required fixture scenario coverage;
- path containment and Docker resource-name safety;
- command argument construction without shell interpolation;
- phase transitions, deadlines, cancellation, diagnostics, and cleanup selection;
- release-baseline refresh rules, including exclusion of the candidate currently being certified;
- deterministic fixture output comparison.

Release-contract tests prove that relevant branch validation invokes fast checks and that promotion depends on the successful full rehearsal and moving-baseline verifier.

### Full Docker rehearsal

The integration workflow proves:

- the fixture restores into clean PostgreSQL and MinIO volumes;
- exact published `0.1.3` validates the baseline fixture;
- the candidate applies all v1 migrations and becomes ready;
- all candidate invariants and exact media hashes pass;
- the candidate accepts and returns a canary write;
- the matched pre-upgrade backup restores into fresh volumes;
- exact published `0.1.3` validates the restored rollback state;
- the canary is absent after rollback;
- a second clean rehearsal produces the same result without using leftover state.

The workflow retains sanitized diagnostics as an artifact when any phase fails. Docker/Testcontainers unavailability is reported as an environment limitation locally, but CI does not skip the job.

### Completion validation

Task 9 is complete only when all of the following pass from a clean working state:

- upgrade-test fast tests and fixture verification;
- deterministic fixture regeneration check;
- the complete baseline/upgrade/rollback Docker rehearsal against exact images;
- release-contract tests and structural workflow verification;
- relevant Infrastructure and API integration tests;
- `dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal`;
- any TypeScript checks required if the release workflow or root JavaScript package metadata changes.

## Operations and evidence

The operations guide will document:

- prerequisites and exact local commands;
- how to inspect fixture provenance and checksums;
- how to refresh the fixture after a stable `0.1.x` publication;
- how to build or select the candidate image;
- what each rehearsal phase proves;
- where sanitized failure artifacts are stored;
- why rollback restores both database and media from the same pre-upgrade generation;
- why old binaries must not be started against v1-written state;
- how installations older than the recorded baseline first upgrade to that published baseline, verify health and backups, and then proceed to v1;
- the CI run and exact image/source identities that constitute release evidence.

The remediation handoff will record the final fixture baseline, image digests, source commit, commands, test counts, CI workflow, and any environmental limitation. No release is represented as certified merely because the harness exists; only a successful run against the exact candidate is evidence.

## Security and data hygiene

- Fixture data is synthetic and contains no real users, customer domains, credentials, or content.
- Any reversible fixture encryption uses a clearly named test-only key that is accepted only by the isolated harness configuration.
- The historical API has no unrestricted outbound network path. Webhook fixtures cannot deliver to external destinations.
- Logs and diagnostic artifacts are allow-listed rather than produced from complete environment or row dumps.
- All image provenance uses immutable digests; tags are descriptive aliases only.
- Cleanup is label- and run-scoped, and all filesystem mutation is path-contained.

## Acceptance criteria

- A reviewer can trace every fixture file to `SHA256SUMS` and every baseline artifact to a published version, immutable image digest, platform, and source commit.
- The fixture represents every domain concern listed in Task 9 with stable expected assertions.
- The exact baseline image validates the fixture before candidate execution.
- The current candidate migrates and passes all required invariants and media checks.
- The candidate proves post-upgrade read/write behavior.
- A matched pre-upgrade database/media backup restores successfully under the exact baseline image in fresh volumes.
- Release promotion cannot bypass the rehearsal or use a stale latest-published `0.1.x` fixture.
- Local and CI commands are documented, deterministic, bounded, and safe to clean up.
- Required focused and full validation passes, with Docker limitations reported rather than silently skipped.
