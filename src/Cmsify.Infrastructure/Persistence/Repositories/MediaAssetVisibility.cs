using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

internal static class MediaAssetVisibility
{
    internal static IQueryable<MediaAsset> GenericMedia(this IQueryable<MediaAsset> query, CmsifyDbContext db)
    {
        query = query.Where(asset => !asset.StorageKey.ToLower().StartsWith(MediaStorageKeys.PhotoPrefix) &&
            !asset.StorageKey.Contains("//") && !asset.StorageKey.Contains("\\"));
        return db.Database.IsNpgsql()
            ? query.Where(asset => !db.PhotoUploadSessions.Any(session => session.AssetId == asset.Id))
            : query;
    }
}
