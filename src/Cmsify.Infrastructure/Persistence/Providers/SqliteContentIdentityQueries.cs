using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteContentIdentityQueries(CmsifyDbContext dbContext) : IContentIdentityQueries
{
    public async Task PropagateAsync(Guid itemId, string? slug, string? localeCode, Guid? translationGroupId, CancellationToken ct)
    {
        var affected = dbContext.ContentVersions.Where(version => version.ContentItemId == itemId);
        await GuardTokensAsync(affected, ct);
        // ExecuteUpdate bypasses tracked revision generation. Identity and its mapped
        // uint token change in the same statement, without loading historical rows.
        await affected.ExecuteUpdateAsync(updates => updates
            .SetProperty(version => version.Slug, slug)
            .SetProperty(version => version.LocaleCode, localeCode)
            .SetProperty(version => version.TranslationGroupId, translationGroupId)
            .SetProperty(version => EF.Property<uint>(version, "xmin"), version => EF.Property<uint>(version, "xmin") + 1u), ct);
    }

    public async Task LinkTranslationsAsync(Guid sourceId, Guid targetId, Guid groupId, CancellationToken ct)
    {
        var affected = dbContext.ContentVersions.Where(version => version.ContentItemId == sourceId || version.ContentItemId == targetId);
        await GuardTokensAsync(affected, ct);
        await affected.ExecuteUpdateAsync(updates => updates
            .SetProperty(version => version.TranslationGroupId, groupId)
            .SetProperty(version => EF.Property<uint>(version, "xmin"), version => EF.Property<uint>(version, "xmin") + 1u), ct);
    }

    private async Task GuardTokensAsync(IQueryable<ContentVersion> affected, CancellationToken ct)
    {
        // The ordinary EF SQLite transaction reserves the writer before mutable
        // reads/guard, so no writer can exhaust a token between guard and update.
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Content identity mutations require a write transaction.");
        if (await affected.AnyAsync(version => EF.Property<uint>(version, "xmin") == uint.MaxValue, ct))
            throw new OverflowException("A content version concurrency token is exhausted.");
    }
}
