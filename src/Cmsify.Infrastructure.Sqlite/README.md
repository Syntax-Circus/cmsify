# Cmsify SQLite infrastructure

Optional SQLite registration for embedded .NET 10 hosts, licensed under AGPL-3.0-or-later.

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

This intermediate source increment is not publishable: the migrator fails closed until the migration baseline is implemented.
