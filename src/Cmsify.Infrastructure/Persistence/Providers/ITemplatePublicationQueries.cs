namespace Cmsify.Infrastructure.Persistence.Providers;

// Infrastructure contract: the caller owns the publication transaction and tracked save.
internal interface ITemplatePublicationQueries
{
    Task ArchivePublishedVersionsAsync(Guid templateId, CancellationToken ct);
}

internal static class TemplatePublicationQueries
{
    public static ITemplatePublicationQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresTemplatePublicationQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteTemplatePublicationQueries(context),
        _ => throw new NotSupportedException($"Unsupported template-publication provider: {context.Database.ProviderName}")
    };
}
