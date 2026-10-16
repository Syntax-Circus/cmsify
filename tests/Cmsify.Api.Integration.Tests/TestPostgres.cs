using Npgsql;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

/// <summary>
/// Drop-in replacement for a per-test <see cref="PostgreSqlContainer"/>: one PostgreSQL container is started
/// lazily per test process and each instance gets its own empty database inside it. Starting a container per test
/// dominated suite runtime; creating a database takes milliseconds. The container is reaped by Testcontainers (Ryuk)
/// when the process exits.
/// </summary>
internal sealed class TestPostgres : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? container;
    private static int databaseCounter;

    private string? databaseName;
    private string? connectionString;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (connectionString is not null)
        {
            return;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (container is null)
            {
                var started = new PostgreSqlBuilder("postgres:17-alpine")
                    .WithDatabase("cmsify")
                    .WithUsername("cmsify")
                    .WithPassword("cmsify")
                    .Build();
                await started.StartAsync(cancellationToken);
                container = started;
            }
        }
        finally
        {
            Gate.Release();
        }

        var name = $"cmsify_test_{Interlocked.Increment(ref databaseCounter)}";
        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        databaseName = name;
        connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public string GetConnectionString() =>
        connectionString ?? throw new InvalidOperationException("Call StartAsync before GetConnectionString.");

    public async ValueTask DisposeAsync()
    {
        if (databaseName is null || container is null)
        {
            return;
        }

        var name = databaseName;
        databaseName = null;
        connectionString = null;

        // Pooled connections from the test's app instance would otherwise keep the database busy.
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
