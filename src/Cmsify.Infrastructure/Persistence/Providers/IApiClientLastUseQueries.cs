namespace Cmsify.Infrastructure.Persistence.Providers;

// Persistence-only strategy. Credential eligibility and snapshot throttling stay in auth.
internal interface IApiClientLastUseQueries
{
    Task<int> TouchIfDueAsync(Guid clientId, DateTimeOffset now, TimeSpan touchInterval, CancellationToken ct);
}

internal static class ApiClientLastUseQueries
{
    public static IApiClientLastUseQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresApiClientLastUseQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteApiClientLastUseQueries(context),
        _ => throw new NotSupportedException($"Unsupported API-client last-use provider: {context.Database.ProviderName}")
    };
}
