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
- Remaining mapped-token bulk gaps: standalone API template publication/package
  import archival (`TemplatesController`, `PackagesController`), content version
  translation/identity propagation (`ContentController`), API client last-use
  touches (`CmsifyOpaqueBearerAuthenticationHandler`), and endpoint secret rotation
  (`WebhookSecretRotationProcessor`). These are not corrected by the repository
  fix; standalone registration still selects PostgreSQL. Other bulk writes need
  their own provider qualification before repository-wide ETag parity is claimed.
- Still unsupported: SQLite migrations/registration, remaining workers' SQL,
  API ILike queries, search parity and complete-engine qualification. Do not expose
  SQLite as a deployment option yet or replace PostgreSQL migrations with EnsureCreated.

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
