# Provider portability work

Requested by the Puppies Plus host owner on 2026-10-01. Work occurs on
`feature/provider-portability` in this checkout; no worktree is used.

## Boundary

Keep domain entities and contracts free of EF/Npgsql/SQLite types and annotations.
Provider-specific column types, value conversion, generated values, concurrency,
search indexes and claims belong in infrastructure. Do not duplicate the domain
entity graph for each database or scatter provider conditionals through entities.

Preserve existing PostgreSQL schema, migration history, ETag/If-Match and API
behavior while extracting provider-specific model configuration. SQLite remains
unqualified until mapping, queries, concurrency, lease fencing, migration/restart,
search and workload tests pass. Model creation alone is not provider support.

## Sequence

1. Extract PostgreSQL-specific model configuration from shared entity mappings.
2. Add SQLite-specific conversion/concurrency with actual persistence tests.
3. Separate search and claim implementations behind infrastructure contracts.
4. Add SQLite migrations and registration; retain PostgreSQL migrations unchanged.
5. Run provider parity and existing PostgreSQL regression suites.

Initial evidence: the unchanged SQLite model fails converting ContentVersion.Tags
(`IList<string>` to PostgreSQL `text[]`). Existing domain CLR types do not reference
Npgsql; the immediate coupling is in EF mappings and SQL operations. SearchVector
also encodes PostgreSQL search syntax and needs a separate semantic review.

## Initial implementation (not full provider support)

- Infrastructure `ICmsifyProviderModel` isolates column types, conversion,
  identity defaults and concurrency generation. PostgreSQL preserves its model;
  SQLite stores JSON/tags as text and timestamps as UTC ticks for ordering.
- The existing `xmin` shadow-property name is retained for compatibility with
  ETag and audit readers; SQLite maps it to `row_version`. It is not a domain
  entity property. Bulk SQL updates must explicitly preserve concurrency semantics.
- Scheduled-publication locking queries are separate provider implementations.
  SQLite uses the ordinary write transaction; PostgreSQL keeps SKIP LOCKED.
- Tests cover tags/JSON persistence, in-place collection changes, stale updates,
  expired claim recovery, fenced completion, single outbox event and unchanged
  PostgreSQL migration snapshot. Full contention/restart tests remain required.
- A four-worker, independent-connection SQLite file test claims a due version
  exactly once. Broader load and forced-restart qualification still remain.
- `TemplateVersionRepository.PublishAsync` now uses infrastructure-only template
  publication queries. SQLite archives the selected template's published versions
  and advances their shadow `xmin`/`row_version` in one bulk statement. Its write
  transaction guards against `uint.MaxValue` before updating, and publication,
  archival and the current-version pointer commit together. An exhausted token
  fails with `OverflowException`; it cannot wrap or leave partial archival when
  the repository owns the transaction. Existing caller-owned transactions retain
  their commit/rollback responsibility. PostgreSQL keeps its status-only update
  and database-generated `xmin`.
- Real file-backed SQLite and migrated PostgreSQL repository tests cover stale
  saves, another template and older archived versions. SQLite exhaustion tests
  cover a mixed set of published versions and rollback when the later tracked
  draft/template save overflows. Both providers also verify caller-owned
  transactions: successful publication remains uncommitted, a later tracked
  unique-constraint failure leaves the transaction with the caller, and caller
  rollback restores version status/tokens, the draft and template pointer.
  Tracked-save generation is unchanged; no operation-level savepoint is promised.
- Remaining mapped-token bulk gaps: content version
  translation/identity propagation (`ContentController`), API client last-use
  touches (`CmsifyOpaqueBearerAuthenticationHandler`). These are not corrected by the repository
  fix; standalone registration still selects PostgreSQL. Other bulk writes need
  their own provider qualification before repository-wide ETag parity is claimed.
- Still unsupported: SQLite migrations/registration, remaining workers' SQL,
  API ILike queries, search parity and complete-engine qualification. Do not expose
  SQLite as a deployment option yet or replace PostgreSQL migrations with EnsureCreated.

## Bounded API template archival integrity repair

The `feature/sqlite-api-template-archival` source increment routes both
`TemplatesController.Publish` and package import's template archival through
the Infrastructure `ArchivePublishedTemplateVersionsAsync` context extension.
It reuses the existing internal template-publication queries. SQLite archives
and increments mapped revisions in the same bulk statement, after guarding the
entire selected published set against `uint.MaxValue` inside the write
transaction. PostgreSQL retains its status-only SQL and generated `xmin`.
No Core repository contract, host/DI default, mapping or migration changes.

The API publication and import operations use the narrowly scoped Infrastructure
`TemplatePublicationWriteScope`. Publication begins before loading mutable
state. Import begins after manifest-only validation and before database reads;
its earlier picklist/revision and component saves now participate in the same
transaction as template archival, new versions and final current pointers.
Only successful final save completes the scope. Error returns, exceptions and
cancellation roll back the operation. This whole-import transaction correction
is intentional: the previous sequence could commit related rows before returning
a later conflict or failure.

An owned scope commits its transaction. A caller-owned transaction uses a unique
operation savepoint, retaining caller commit ownership and earlier work; a caller
transaction without savepoint support is rejected before operation writes.
Failure cleanup uses a non-cancelled token. A private tracker checkpoint restores
preexisting current/original scalar values using configured EF value comparers,
navigation contents, states and modified/temporary flags, and detaches only
operation-introduced entries after normal relationship change detection.
This prevents a later unrelated caller save from replaying rolled-back import
writes. It is bounded to Cmsify's present flat scalar properties and CLR
reference/collection navigations, not a general EF snapshot framework. The
repository's existing caller rollback contract remains unchanged.

`TemplateArchivalControllerTests` execute actual production publication and JSON
import methods with independent file-backed SQLite contexts and PostgreSQL
Testcontainers with actual migrations. They cover stale rejection, exact SQLite
revision increments, unaffected archived/draft/other-template rows, new and
existing template imports, mixed-set and later-template exhaustion, later
tracked-save exhaustion, final constraint/concurrency/cancellation failures,
rollback after earlier related saves, unresolved/invalid component resolutions,
outbox preservation, caller rollback/commit ownership and caller tracker reuse.
With 34 selected published versions, SQLite tests require one bulk update per
template and zero historical version materialization (only the publication
target draft is materialized). These are blocking count assertions, not latency
claims. Scoped SQLite `EnsureCreated` is a test adaptation, not production host
registration or migration evidence. Existing HTTP authorization/error tests
remain part of the API regression suite.

This is a bounded legacy persistence repair, not a controller workflow extraction
or proof of Puppies Plus handler architecture. Full application extraction,
SQLite migrations/registration, other mapped-token bulk paths, search, crash
recovery and complete-engine qualification remain open. This unreleased source
increment does not change the Puppies Plus public 0.8.4 package evidence or
enable SQLite deployment.

## Bounded webhook secret-rotation repair

The `feature/sqlite-secret-rotation-portability` source increment makes
`WebhookSecretRotationProcessor.RotateBatchAsync` and `CountRemainingAsync`
portable through Infrastructure query strategies. The processor still owns its
transaction, crypto orchestration, counters and sanitized logging. PostgreSQL
retains its existing SQL and defaults.

SQLite reserves the writer before selecting a bounded batch and holds it through
the conditional original-secret updates and commit. Rotation increments the
mapped `row_version` atomically, rejects an exhausted selected token before any
batch writes, and stores timestamps in the existing UTC-tick representation.
Counts are aggregated and ordered in the database using only existing bounded
version/configured-key labels. Raw ciphertext is not returned by the count query.

Real file-backed SQLite tests use independent connections and cover plaintext
preservation, legacy/old-key rewrap, active-prefix exclusion, soft deletion,
cursor/batch bounds, fresh-context resume, invalid/canceled work, sanitized
decrypt failures, competing writers, conditional-update skips, stale tracked
saves, token exhaustion and database-failure rollback. Existing migrated
PostgreSQL rotation and worker-lifecycle tests remain in the focused regression.
Fresh-context resume is not process-crash durability evidence. SQLite
`EnsureCreated` is test-only; this increment adds no migrations, provider
registration, encryption format/key changes or deployment qualification.

## Bounded media reconciliation repair

The `feature/sqlite-media-reconciliation-portability` source increment separates
`MediaReconciliationRepository` SQL into Infrastructure-only provider queries.
PostgreSQL retains its existing locking SQL; repository contracts, processor
behavior, mappings, migrations and registration defaults are unchanged.

SQLite's repository-owned non-deferred write transaction reserves the writer
before deletion/stale-upload/checkpoint selection and holds it through tracked
state changes and commit. Claim batches retain database limits and their existing
eligibility, order, owner/token/expiry fences and retry counters. Raw orphan and
checkpoint inserts use the mapped GUID parameter representation and UTC ticks,
with the existing pending-intent and provider/prefix conflict predicates.
Intent/checkpoint entities have no mapped concurrency revision; asset updates
remain tracked and advance `row_version` through the existing save hooks.

Real file-backed SQLite tests cover independent held-selection competition,
nonempty disjoint claims, exact lease expiry/reclaim, stale claims, retry/backoff,
stale upload state/enqueue rollback on an injected database failure, orphan
deduplication and completed history, nonzero-offset timestamp/nontrivial GUID
roundtrips, checkpoint pagination/resume/reset and bounded materialization.
Synthetic processor cycles retain newly owned blobs and persist retry and scan
progress. Existing migrated PostgreSQL media tests remain in the regression.

This is bounded repository/processor evidence, not full SQLite deployment or
all-worker support. `EnsureCreated` remains test-only. Production SQLite
migrations/registration, broader workload/crash recovery, search parity and
consistent blob/database backup qualification remain open.

## Verification checkpoint

Full solution: `rtk proxy dotnet test --solution Cmsify.slnx --configuration Release
--no-build` — 997 passed, zero failed/skipped (10m58s). Four focused provider tests
pass in Debug, including the subsequently added independent-connection claim test.
Puppies Plus actual-source host probes pass PostgreSQL migrations/publishing and
SQLite test-schema/publishing. No production migrations, deployment or package
publication have been performed. Full provider support remains gated above.

## Embedded host identity and worker registration foundation

`AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all six hosted
workers. A host can opt into its scoped `ICurrentActor` for audit attribution and
select workers independently at registration time:

```csharp
services.AddScoped<ICurrentActor, HostCurrentActor>();
services.AddCmsifyInfrastructure(configuration, new CmsifyInfrastructureOptions
{
    UseHostCurrentActorForAudit = true,
    Workers = CmsifyWorkers.ScheduledPublishing | CmsifyWorkers.WebhookDispatch
});
```

`CmsifyWorkers.None` registers no workers; `CmsifyWorkers.All` is the default.
The other independent selections are `MediaReconciliation`, `WebhookRetry`,
`WebhookSecretRotation`, and `WebhookSecretRotationInventoryPreflight`.
Selections are fixed when services are registered. Operational options still
configure the selected workers; resolving services does not migrate or seed the
database. Migration and seeding remain explicit host lifecycle operations.

Audit interception consumes Core's transport-neutral `IAuditActorAccessor`, which
returns only nullable user/API-client IDs and grants no permissions. Host audit
mode uses `HostCurrentActorAuditAccessor`; unauthenticated actors yield no IDs,
including stale IDs. A missing host actor defaults to anonymous, even if HTTP
actor data exists. Register the host actor in the composition root; default actor
and audit adapters use `TryAdd` to preserve deliberate host registrations.

Standalone mode uses `HttpAuditActorAccessor`: an authenticated HTTP item takes
precedence, followed by an authenticated principal's valid API-client claim,
then `NameIdentifier`, `sub`, and `cmsify_user_id` in that order. The first present
user claim is parsed, so an invalid higher-priority claim does not fall through.
No HTTP context yields null attribution. The legacy HTTP-accessor constructor
of `AuditInterceptor` remains available for direct construction.

Repeated calls with identical infrastructure options and configuration values
return without adding registrations, workers, option bindings or validators.
Conflicting options or configuration values throw `InvalidOperationException`;
the host must choose one composition configuration. Configuration equality here
includes the full supplied configuration snapshot at each registration call.

This foundation does not qualify a complete embedded engine or SQLite deployment.
It does not change workspace authorization, host grants, authentication schemes,
provider selection, migrations, or package versions.

## Direct workspace workflows

Infrastructure composition registers Core's five `IWorkspaces*RequestHandler`
interfaces (`List`, `Create`, `Get`, `Update`, `Delete`) in `Cmsify.Core.Workspaces`.
Hosts call `HandleAsync` with application-owned request records and their scoped
`ICurrentActor`; no HTTP context, SDK, local session or second identity authority
is required. Expected failures use public `SyntaxCircus.Common` **0.1.3** results.
This qualification covers workspaces only; other management workflows still use
their existing entry points.

Anonymous actors are denied. Mutations require `Admin`; creation additionally
requires super-admin. Reads preserve existing workspace scope and write capability.
Updates/deletion hide inaccessible or non-writable IDs as not-found. A host can
supply existing workspace scope or super-admin authority; registration creates
no grants. Deletion requires a user subject for soft-delete attribution, so an
actor without one receives an expected forbidden result.

```csharp
// Inject IWorkspacesUpdateRequestHandler into the host entry point as updateWorkspace.
var result = await updateWorkspace.HandleAsync(new WorkspacesUpdateRequest(
        workspace.Id, "New name", workspace.Slug, workspace.Description,
        workspace.Revision), cancellationToken);
```

Update and delete requests carry nullable expected revisions (UTC ticks), while
HTTP retains exact quoted-tick ETag/If-Match parsing. Outputs include workspace
data, `Revision`, and `CanWrite`; list output includes page, page size and total.
The returned revision can be used immediately for another mutation. Workspace
creation and revision-checked mutations normalize timestamps to PostgreSQL
microseconds, and every successful mutation advances the revision by at least
one microsecond even when the clock has not advanced. Missing/stale revisions
are conflicts. Existing name/slug/description command constraints apply after
authorization and, on update, resource visibility and revision checks.

`IWorkspaceMutationRepository` is limited to atomic revision-checked update and
delete. Infrastructure uses the loaded `xmin` token inside a transaction;
concurrent losers return conflict. Update persists the existing
`workspace.updated` outbox payload and audit with the workspace, or none of them.
Deletion atomically soft-deletes with actor attribution. The PostgreSQL integration
suite verifies successive updates, actual outbox constraint failure rollback and
two gated contenders for one revision (update/update and update/delete), plus
external subject audit with empty local user/session tables. SQLite remains
unqualified for deployment as described above.
