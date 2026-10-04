using System.Data;
using System.Data.Common;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

internal static class SqliteCmsifySchemaGuard
{
    internal static async Task ValidateAsync(CmsifyDbContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Run Cmsify SQLite migrations outside an application transaction.");
        var connection = context.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(cancellationToken);
        try
        {
            // A deferred read transaction gives schema/history a consistent snapshot without
            // taking a competing write lock. Controlled deployment requires the same migrator
            // version and forbids concurrent external schema edits.
            await using var transaction = ((Microsoft.Data.Sqlite.SqliteConnection)connection).BeginTransaction(deferred: true);
            var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = Command(connection, transaction, "SELECT name, type FROM sqlite_schema WHERE type IN ('table', 'view')"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) objects.Add(reader.GetString(0), reader.GetString(1));

            var applied = new List<string>();
            if (objects.TryGetValue(SqliteCmsifyDbContextOptions.MigrationsHistoryTable, out var historyType))
            {
                if (historyType != "table") throw MalformedHistory();
                var columns = new Dictionary<string, (string Type, bool Required, bool Primary)>(StringComparer.OrdinalIgnoreCase);
                await using (var command = Command(connection, transaction, "PRAGMA table_info(\"__CmsifyMigrationsHistory\")"))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1),
                        (reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(5) == 1));
                if (columns.Count != 2
                    || !columns.TryGetValue("migration_id", out var id) || id.Type != "TEXT" || !id.Required || !id.Primary
                    || !columns.TryGetValue("product_version", out var version) || version.Type != "TEXT" || !version.Required || version.Primary)
                    throw MalformedHistory();
                await using (var command = Command(connection, transaction,
                    "SELECT \"migration_id\", \"product_version\" FROM \"__CmsifyMigrationsHistory\" ORDER BY \"migration_id\""))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(0))
                            || !Version.TryParse(reader.GetString(1), out _)) throw MalformedHistory();
                        applied.Add(reader.GetString(0));
                    }
            }

            var ownedTables = context.Model.GetRelationalModel().Tables.Select(table => table.Name);
            if (applied.Count == 0 && ownedTables.Any(objects.ContainsKey))
                throw new InvalidOperationException("Unmanaged Cmsify SQLite schema detected. Restore a migration-managed backup or use a fresh database; automatic schema adoption is unsupported.");
            var known = context.Database.GetMigrations().ToArray();
            if (known.Length == 0) throw new InvalidOperationException("No Cmsify SQLite migrations are available in the selected migration assembly.");
            if (applied.Count > known.Length || !applied.SequenceEqual(known.Take(applied.Count), StringComparer.Ordinal))
                throw new InvalidOperationException("Cmsify SQLite migration history is unknown or gapped. Use compatible application binaries or restore a verified backup; automatic downgrade is unsupported.");
            if (applied.Count > 0)
            {
                var assembly = context.GetService<IMigrationsAssembly>();
                var migration = assembly.CreateMigration(assembly.Migrations[applied[^1]], context.Database.ProviderName!);
                // Check the last applied schema, not the latest runtime model: pending
                // migrations may legitimately add or remove tables.
                var missing = migration.TargetModel.GetEntityTypes().Select(entity => entity.GetTableName())
                    .Where(name => name is not null).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => !objects.TryGetValue(name!, out var type) || type != "table").Order().ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException($"Cmsify SQLite schema is missing tables recorded by its migration history: {string.Join(", ", missing)}. Restore a verified backup; automatic repair is unsupported.");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (closeConnection) await connection.CloseAsync();
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static InvalidOperationException MalformedHistory() => new(
        "Malformed Cmsify SQLite migration history. Inspect __CmsifyMigrationsHistory and restore a verified backup; automatic repair is unsupported.");
}
