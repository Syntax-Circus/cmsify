using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Sqlite.Persistence;

internal static class SqliteCmsifyDbContextOptions
{
    internal const string MigrationsHistoryTable = "__CmsifyMigrationsHistory";
    private const int DefaultTimeoutSeconds = 30;

    internal static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connection = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.DataSource)
            || connection.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || connection.Mode == SqliteOpenMode.Memory
            || connection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cmsify SQLite requires a local persistent file data source; memory and URI data sources are unsupported.", nameof(connectionString));
        if (connection.ForeignKeys == false)
            throw new ArgumentException("Cmsify SQLite requires foreign-key enforcement.", nameof(connectionString));
        connection.ForeignKeys = true;
        if (!connection.ContainsKey("Default Timeout")) connection.DefaultTimeout = DefaultTimeoutSeconds;
        options.UseSqlite(connection.ConnectionString, sqlite => sqlite
                .MigrationsAssembly(typeof(SqliteCmsifyDatabaseProvider).Assembly.GetName().Name)
                .MigrationsHistoryTable(MigrationsHistoryTable)
                .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            // Use the provider-neutral convention: the PostgreSQL wrapper replaces
            // migration history services and would override SQLite's migration lock.
            .UseSnakeCaseNamingConvention();
    }
}
