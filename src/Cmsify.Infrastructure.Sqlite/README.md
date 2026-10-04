# Cmsify SQLite infrastructure

Optional SQLite registration for embedded .NET 10 hosts, licensed under AGPL-3.0-or-later.

See the [embedded SQLite guide](https://github.com/Syntax-Circus/cmsify/blob/main/docs/integrations/embedded-sqlite.md)
for an executable host example, explicit initialization, deployment constraints,
backup rollback and abandoned-lock recovery. This package is released in lockstep
with Core and Infrastructure. Local candidate checks do not establish publication
or full SQLite qualification; generic JSON queries, complete workers, production
backup/blob recovery and supported-version upgrades remain separate gates.

## Bounded JSON string predicates

Optional SQLite registration translates `JsonElement.GetProperty(string)` object
traversal ending in `GetString()` into native SQLite JSON functions. For example:

```csharp
var count = await db.TemplateFields.CountAsync(row => row.FieldConfig.HasValue
    && row.FieldConfig.Value.GetProperty("name").GetString() == expected);
```

Traversal roots must be direct mapped `JsonElement`/`JsonElement?` properties on
direct query entity parameters, accessed by a CLR member or `EF.Property` with a constant mapped
property name. Computed roots (for example, deserializing a string column), aliases
and arbitrary JSON-producing expressions are unsupported. JSON-producing non-EF
methods, JsonDocument members, JSON casts and construction of mapped entities with
JSON properties are rejected in queries, including composed projection aliases.
Chained object properties,
constant or parameterized names, and parameterized
comparison values are supported. Names are JSON-quoted SQL values, including
Unicode, empty names, quotes, backslashes, dots and brackets; names never become
SQL fragments. Filtering and aggregates execute in SQLite. Each traversal step
preserves JSON structure, so a string containing serialized JSON is not an object.

Missing properties, JSON null, and traversal through a non-object produce SQL null.
An SQL-null document also produces SQL null. `HasValue` excludes SQL-null documents;
without that guard, EF null comparison semantics can include them. These are
database query semantics, not CLR `GetProperty` exception semantics.

Terminal JSON strings and null are the bounded supported values. Numbers,
booleans, objects and arrays at the terminal property raise a SQLite error whose
message contains `Cmsify SQLite GetString supports only JSON strings or null`.
They are neither cast into strings nor silently returned as null. PostgreSQL's
existing native text extraction remains unchanged and can return text for those
kinds; this package does not promise mixed-kind parity. Typed getters, array
indexing/enumeration, bare `GetProperty`, root `GetString`, and other `JsonElement`
methods and member projections such as `ValueKind` fail explicitly during query
compilation before SQL executes. Whole mapped JSON values, including nullable
values, still round trip normally; inspecting them explicitly after materialization
is a separate caller operation. A null property-name parameter
raises a native SQLite error containing `Cmsify SQLite GetProperty requires a non-null name`;
the actual string name `"null"` remains supported.
There is no automatic client evaluation fallback. This bounded translation does
not establish generic JSON-query support, JSON indexing, performance guarantees,
or complete provider qualification.

Local candidate `0.8.9-query.1` adds provider-specific ordinary/resolved content
queries through the registered `Cmsify.Core.ContentQueries.IListContentRequestHandler`.
It qualifies actual snapshot all-tag membership, measured native LIKE semantics,
fixed host clock/AsOf, paging and denial in a package-only host. Released 0.8.8 lacks
this public contract; generic JSON LINQ translation remains a separate open gap.

Reference `SyntaxCircus.Cmsify.Infrastructure.Sqlite` and import
`Cmsify.Infrastructure.Sqlite.Extensions`. Register with
`services.AddCmsifySqliteInfrastructure(configuration)` or pass the common
`CmsifyInfrastructureOptions` to select workers and host audit identity.
The default remains all six workers and HTTP actor audit, matching PostgreSQL.

Set `ConnectionStrings:Cmsify` to `Data Source=/local/persistent/cmsify.db`.
Only local persistent file data sources are supported. Memory and URI data sources
are rejected. Foreign keys are enabled; explicit `Foreign Keys=False` is rejected.
The default timeout is 30 seconds; an explicit caller timeout is preserved.
Registration and context resolution do not create a database, run migrations,
seed data or change journal mode. Identical registration is a no-op; provider,
configuration or common-option conflicts fail before changing registrations.

SQLite migration is schema-only and will not provision a workspace or administrator.
Explicit initialization with the existing `IDbSeeder` is a separate operator step.

Run `host.MigrateCmsifyDatabaseAsync()` as a controlled deployment step before
workers or traffic. The package owns `__CmsifyMigrationsHistory`; its columns are
`migration_id` and `product_version` under the provider-neutral snake-case convention.
SQLite retains its native EF migration history service and locking. PostgreSQL's
history naming and service remain unchanged.

Migration rejects unmanaged CMS tables, malformed or unknown/gapped CMS history,
and tables missing from the last applied migration's model. Host tables and host
migration histories are retained. These ownership and table-presence checks are
not an exhaustive schema integrity audit. Schema/history are read in one transaction;
the transaction ends before EF acquires its migration lock and re-reads history.
Concurrent migrators must run identical application versions. Controlled deployment
must prohibit simultaneous different-version migrators and external schema edits.

Design-time commands must select this project as both project and startup project:
`dotnet ef migrations list --project src/Cmsify.Infrastructure.Sqlite --startup-project src/Cmsify.Infrastructure.Sqlite --context CmsifyDbContext`.
The factory defaults to the development-only `cmsify.design-time.db` file. Pass an
explicit connection string after `--` to select a different local development file.
