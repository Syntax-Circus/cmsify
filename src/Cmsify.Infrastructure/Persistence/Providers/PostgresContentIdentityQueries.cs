using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresContentIdentityQueries(CmsifyDbContext dbContext) : IContentIdentityQueries
{
    // PostgreSQL advances xmin itself; preserve the existing field-only statements.
    public Task PropagateAsync(Guid itemId, string? slug, string? localeCode, Guid? translationGroupId, CancellationToken ct) =>
        dbContext.ContentVersions.Where(version => version.ContentItemId == itemId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(version => version.Slug, slug)
                .SetProperty(version => version.LocaleCode, localeCode)
                .SetProperty(version => version.TranslationGroupId, translationGroupId), ct);

    public Task LinkTranslationsAsync(Guid sourceId, Guid targetId, Guid groupId, CancellationToken ct) =>
        dbContext.ContentVersions.Where(version => version.ContentItemId == sourceId || version.ContentItemId == targetId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(version => version.TranslationGroupId, groupId), ct);
}
