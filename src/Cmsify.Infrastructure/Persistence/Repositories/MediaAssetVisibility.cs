using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public static class MediaAssetVisibility
{
    public static IQueryable<MediaAsset> GenericMedia(this IQueryable<MediaAsset> query, CmsifyDbContext db)
    {
        query = query.Where(asset => !asset.StorageKey.ToLower().StartsWith(MediaStorageKeys.PhotoPrefix) &&
            !asset.StorageKey.ToLower().StartsWith(MediaStorageKeys.FilePrefix) &&
            asset.StorageKey!="" && !asset.StorageKey.Contains("//") && !asset.StorageKey.Contains("\\") && !asset.StorageKey.Contains(":") &&
            !asset.StorageKey.StartsWith("/") && !asset.StorageKey.EndsWith("/") && !asset.StorageKey.Contains("./") &&
            !asset.StorageKey.Contains(" /") && !asset.StorageKey.EndsWith(".") && !asset.StorageKey.EndsWith(" "));
        query=query.FileUnowned(db);
        return db.Database.IsNpgsql()
            ? query.Where(asset => !db.PhotoUploadSessions.Any(session => session.AssetId == asset.Id))
            : query;
    }
    internal static IQueryable<MediaAsset> FileUnowned(this IQueryable<MediaAsset> query,CmsifyDbContext db)=>!db.Database.IsNpgsql()?query:
        query.Where(asset=>!db.FileUploadSessions.Any(session=>session.AssetId==asset.Id ||
            db.MediaAssets.IgnoreQueryFilters().Any(owner=>owner.Id==session.AssetId && owner.StorageProvider.ToLower()==asset.StorageProvider.ToLower() && owner.StorageKey.ToLower()==asset.StorageKey.ToLower())) &&
            !db.FileUploadReceipts.Any(receipt=>receipt.StorageProvider.ToLower()==asset.StorageProvider.ToLower() && receipt.StorageKey.ToLower()==asset.StorageKey.ToLower()) &&
            !db.FileUploadAttempts.Any(attempt=>attempt.StorageProvider.ToLower()==asset.StorageProvider.ToLower() && attempt.StorageKey.ToLower()==asset.StorageKey.ToLower()));

    internal static async Task<bool> FileOwnsKeyAsync(CmsifyDbContext db,string provider,string key,CancellationToken ct)
    {
        if(!db.Database.IsNpgsql())return false;
        provider=provider.ToLowerInvariant();key=key.ToLowerInvariant();
        return await db.FileUploadSessions.AnyAsync(session=>db.MediaAssets.IgnoreQueryFilters().Any(asset=>asset.Id==session.AssetId && asset.StorageProvider.ToLower()==provider && asset.StorageKey.ToLower()==key),ct) ||
            await db.FileUploadReceipts.AnyAsync(receipt=>receipt.StorageProvider.ToLower()==provider && receipt.StorageKey.ToLower()==key,ct) ||
            await db.FileUploadAttempts.AnyAsync(attempt=>attempt.StorageProvider.ToLower()==provider && attempt.StorageKey.ToLower()==key,ct);
    }
}
