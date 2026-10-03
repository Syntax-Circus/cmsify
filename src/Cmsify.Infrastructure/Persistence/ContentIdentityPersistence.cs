using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence.Providers;

namespace Cmsify.Infrastructure.Persistence;

/// <summary>Bounded persistence operations for legacy API content identity mutations.</summary>
public static class ContentIdentityPersistence
{
    /// <summary>
    /// Propagates the item's denormalized identity to every version. Version tags
    /// remain immutable snapshots. Caller holds the transaction across tracked saves.
    /// </summary>
    public static Task PropagateContentIdentityAsync(this CmsifyDbContext context, ContentItem item, CancellationToken ct)
    {
        RequireTransaction(context);
        return ContentIdentityQueries.Create(context).PropagateAsync(item.Id, item.Slug, item.LocaleCode, item.TranslationGroupId, ct);
    }

    /// <summary>Updates only the source/target versions' translation group within the caller's write transaction.</summary>
    public static Task LinkContentVersionTranslationsAsync(this CmsifyDbContext context, Guid sourceId, Guid targetId, Guid groupId, CancellationToken ct)
    {
        RequireTransaction(context);
        return ContentIdentityQueries.Create(context).LinkTranslationsAsync(sourceId, targetId, groupId, ct);
    }

    private static void RequireTransaction(CmsifyDbContext context)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Content identity mutations require a write transaction.");
    }
}
