# Embedded SQLite hosts

`SyntaxCircus.Cmsify.Infrastructure.Sqlite` is an optional .NET 10 engine package
released in lockstep with Core and Infrastructure. It uses AGPL-3.0-or-later.
Existing Infrastructure consumers retain PostgreSQL without SQLite/native assets;
SQLite consumers still receive Npgsql through Infrastructure. Local candidate
`0.8.8-sqlite.1` is a verification identifier, not a selected next release version
or proof of publication. Reference an independently verified matching package version.

## Register and migrate before starting

Configure `ConnectionStrings:Cmsify` as `Data Source=/local/persistent/cmsify.db`.
The host must register its scoped `ICurrentActor` before selecting host audit mode.

```csharp
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Sqlite.Extensions;

var builder = WebApplication.CreateBuilder(args);
// Register the host's scoped ICurrentActor implementation here.
builder.Services.AddCmsifySqliteInfrastructure(builder.Configuration,
    new CmsifyInfrastructureOptions
    {
        UseHostCurrentActorForAudit = true,
        Workers = CmsifyWorkers.None
    });
await using var app = builder.Build();
await app.MigrateCmsifyDatabaseAsync(); // Failure aborts startup/readiness.
await app.RunAsync();
```

Registration and context resolution perform no database I/O, migrations or seeding.
Identical registration is a no-op; conflicting or mixed providers fail before
service mutation. Common worker/audit defaults match PostgreSQL: all six workers
and HTTP actor audit unless explicitly selected. Migrate before starting the host,
workers or traffic. SQLite migration creates schema only, with no default workspace
or administrator and no administrator password requirement. Embedded provisioning
is a separate host workflow. An operator may explicitly resolve `IDbSeeder` in
a scope and call `SeedAsync` after migration, with the existing explicit seed
credential configuration, before traffic/workers. Seeding is single-operator;
concurrent initialization is not qualified. PostgreSQL migrate-then-seed is unchanged.

## Database ownership and deployment

Use a local persistent file, never a network filesystem, memory database or URI
data source. Give the service account access to the database and its directory
for SQLite journal/sidecar creation; restrict access to backups and other users.
Foreign keys are enforced; explicit `Foreign Keys=False` is rejected. The managed
command timeout defaults to 30 seconds; an explicit timeout is preserved.
Registration does not rewrite journal mode. Record permissions, journal mode,
foreign-key setting and timeout for the actual deployment. Disposable production
baseline probes measured `journal_mode=delete`, foreign keys `1`, native
`PRAGMA busy_timeout=0` with managed timeout 2 seconds in contention tests;
synthetic upgrade fixtures separately used WAL. These are fixture measurements,
not deployment tuning defaults. Cancellation can wait through the synchronous
SQLite busy timeout; there is no immediate-interruption guarantee. Use an outer
process deadline and report readiness only after migration succeeds.

The package owns `__CmsifyMigrationsHistory` with columns `migration_id` and
`product_version`, a real migration assembly and snapshot. It rejects existing
CMS tables without recognized history, malformed/unknown/gapped history and
missing tables from the applied migration model. It preserves unrelated host
tables/history. The guard is not a complete integrity audit. Do not adopt old
experimental `EnsureCreated` databases, baseline history by hand, or silently
reset a rejected database. Import/conversion requires a separate reviewed workflow.

Serialize CMS and breeder/host migrations against the same file even though their
histories are separate. Use one controlled deployment owner and identical binaries
for any concurrent same-version CMS attempts. Different-version migrators and
external schema changes must never overlap. The guard ends its read transaction
before EF acquires its native migration lock and re-reads history.

## Backup, rollback and abandoned locks

Before an upgrade, stop traffic/workers/migrators and take a consistent, verified
backup using SQLite's backup facilities or a closed database with all required
journal/sidecar state. Record schema/history, binary version and backup checksums.
Rehearse restoration. Rollback restores the verified pre-upgrade database with
compatible application binaries; arbitrary down migrations are not promised.
Database/blob reconciliation and production backup recovery remain qualification gates.

EF's SQLite migration lock can survive process death. Never automatically delete
or steal a lock. To recover a proven abandoned lock:

1. Stop every migrator and application writer; verify no live process owns the file.
2. Back up the closed database and preserve failure logs and original files.
3. Inspect `__CmsifyMigrationsHistory`, schema and integrity against the exact
   application's migration chain. If partial migration state is uncertain, restore
   the verified backup with compatible binaries instead of asserting completion.
4. Only after proving abandonment and consistency, an operator may clear EF's
   `__EFMigrationsLock` table explicitly, following the pinned provider's recovery
   instructions, then retry the migration once under exclusive ownership.
5. Verify complete recognized history, integrity, retained data and application
   readiness before reopening traffic. Preserve the backup until acceptance.

The source recovery probes cover disposable synthetic table rebuild/failure/death
points and real-baseline same-version contention. The first SQLite baseline has
no earlier supported SQLite release to upgrade from; synthetic upgrades do not
prove a deployed-version upgrade path or arbitrary crash-window safety.

## Qualification limits

The clean candidate consumer proves packaged registration, native Windows/Linux
amd64 loading, real schema-only migration, idempotent restart and bounded workspace
CRUD/audit/stale-revision behavior. It does not qualify every repository, JSON
query translation, full workers, authentication, capacity or
production recovery. JSON storage round trips do not establish JSON query/search
parity. Full SQLite qualification remains open. See [engine packages](../engine-packages.md)
and [provider portability](../provider-portability.md).

The local `0.8.9-query.1` candidate additionally exposes the registered
`Cmsify.Core.ContentQueries.IListContentRequestHandler` for ordinary and resolved
content lists in process. Supply a scoped host `ICurrentActor`; optionally register
a host `TimeProvider` before infrastructure (the default is `TimeProvider.System`).
Use `ListContentRequest(workspaceId, Resolve: true, AsOf: instant)` to evaluate
serving snapshots; ordinary mode uses the clock and item metadata/live tag links.
The package consumer checks both modes, actual snapshot all-tag membership,
provider-specific Q behavior, paging and denial without starting a Cmsify API.
This is candidate evidence only: released 0.8.8 lacks the public contract. Native
SQLite LIKE retains its measured Unicode/case differences from PostgreSQL; generic
JSON LINQ translation, full embedding and production qualification remain open.

Source SQLite registration now supports a bounded native JSON string-predicate
shape: one or more `GetProperty(string)` object traversals ending in `GetString()`.
See the [SQLite package README](../../src/Cmsify.Infrastructure.Sqlite/README.md#bounded-json-string-predicates)
for parameterization, missing/null semantics, explicit mixed-kind errors and
unsupported expressions. This is source evidence, not a public-release adoption
claim; broader JSON queries and full provider qualification remain open.

Design-time commands select the SQLite project as both project and startup project:

```powershell
dotnet ef migrations list --project src/Cmsify.Infrastructure.Sqlite --startup-project src/Cmsify.Infrastructure.Sqlite --context CmsifyDbContext
```

The factory's `cmsify.design-time.db` default is development-only. Pass an explicit
local development connection string after `--` for another file.
