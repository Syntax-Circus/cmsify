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
- Known remaining concurrency gap: bulk `ExecuteUpdateAsync` paths bypass tracked
  version preparation (including template archival). SQLite token advancement for
  those paths must be implemented before repository-wide ETag parity is claimed.
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
