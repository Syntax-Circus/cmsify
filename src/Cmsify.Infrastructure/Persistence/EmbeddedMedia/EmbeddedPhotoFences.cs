using Cmsify.Core.Domain.Entities;
using Cmsify.Core.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal static class EmbeddedPhotoFences
{
    internal static async Task<Guid?> FindAssetForKeyAsync(CmsifyDbContext db, string provider, string key, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return null;
        return await db.PhotoOriginals.Where(x => x.StorageProvider == provider && x.StorageKey == key).Select(x => (Guid?)x.AssetId).FirstOrDefaultAsync(ct)
            ?? await db.PhotoVariants.Where(x => x.StorageProvider == provider && x.StorageKey == key).Select(x => (Guid?)x.AssetId).FirstOrDefaultAsync(ct)
            ?? await db.PhotoUploadAttempts.Where(x => x.StorageProvider == provider && x.StorageKey == key)
                .Join(db.PhotoUploadSessions, x => x.SessionId, x => x.Id, (attempt, session) => (Guid?)session.AssetId).FirstOrDefaultAsync(ct)
            ?? await db.PhotoProcessingAttempts.Where(x => x.StorageProvider == provider && x.StorageKey == key)
                .Join(db.PhotoJobs, x => x.JobId, x => x.Id, (attempt, job) => (Guid?)job.AssetId).FirstOrDefaultAsync(ct);
    }
    internal static async Task<MediaAsset?> LockAssetAsync(CmsifyDbContext db, Guid id, CancellationToken ct)
    {
        var query = db.Database.IsNpgsql()
            ? db.MediaAssets.FromSqlInterpolated($"SELECT *, xmin FROM media_assets WHERE id = {id} FOR UPDATE").IgnoreQueryFilters().AsNoTracking()
            : db.MediaAssets.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == id);
        var row = await query.Select(x => new { Asset = x, Version = EF.Property<uint>(x, "xmin") }).SingleOrDefaultAsync(ct);
        var fresh = row?.Asset;
        if (fresh is null) return null;
        var local = db.MediaAssets.Local.FirstOrDefault(x => x.Id == id);
        if (local is null) { db.MediaAssets.Attach(fresh); local = fresh; }
        else { db.Entry(local).CurrentValues.SetValues(fresh); db.Entry(local).OriginalValues.SetValues(fresh); }
        db.Entry(local).Property("xmin").CurrentValue = row!.Version;
        db.Entry(local).Property("xmin").OriginalValue = row.Version;
        db.Entry(local).State = EntityState.Unchanged;
        return local;
    }
    internal static Task<bool> ProtectedAsync(CmsifyDbContext db, Guid asset, CancellationToken ct) =>
        db.Database.IsNpgsql()
            ? ProtectedPhotoAsync(db, asset, ct)
            : Task.FromResult(false);
    private static async Task<bool> ProtectedPhotoAsync(CmsifyDbContext db, Guid asset, CancellationToken ct) =>
        await db.PhotoIntakeRetentions.AnyAsync(hold => hold.AssetId == asset, ct) ||
        await db.MediaReferenceEntries.AnyAsync(entry => entry.AssetId == asset &&
                db.MediaReferenceSets.Any(set => set.Id == entry.ReferenceSetId && set.State == EmbeddedMediaReferenceState.Protected), ct);
    internal static Task<List<Guid>> ContentReferencesAsync(CmsifyDbContext db, Guid asset, bool includeRecovery, CancellationToken ct) =>
        db.ContentVersionFieldValues.AsNoTracking().Where(value => value.MediaAssetId == asset || value.FileAssetId == asset)
            .Join(db.ContentVersions.AsNoTracking(), value => value.ContentVersionId, version => version.Id, (value, version) => version.ContentItemId)
            .Where(contentId => db.ContentItems.IgnoreQueryFilters().Any(content => content.Id == contentId && (includeRecovery || !content.IsDeleted)))
            .Distinct().ToListAsync(ct);
}
