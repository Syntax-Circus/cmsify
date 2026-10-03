namespace Cmsify.Infrastructure.Persistence.Providers;

// Internal Infrastructure strategy; the legacy controller retains business policy
// and holds the write transaction across item/tag/outbox saves and version updates.
internal interface IContentIdentityQueries
{
    Task PropagateAsync(Guid itemId, string? slug, string? localeCode, Guid? translationGroupId, CancellationToken ct);
    Task LinkTranslationsAsync(Guid sourceId, Guid targetId, Guid groupId, CancellationToken ct);
}

internal static class ContentIdentityQueries
{
    public static IContentIdentityQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresContentIdentityQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteContentIdentityQueries(context),
        _ => throw new NotSupportedException($"Unsupported content-identity provider: {context.Database.ProviderName}")
    };
}
