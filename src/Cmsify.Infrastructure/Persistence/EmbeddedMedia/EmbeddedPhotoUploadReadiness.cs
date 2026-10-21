using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

/// <summary>Read-only prerequisite check. Does not qualify native processing or test storage writes.</summary>
public sealed class EmbeddedPhotoUploadReadiness(CmsifyDbContext db, IStorageProvider storage,
    IOptions<EmbeddedPhotoOptions> options) : IEmbeddedPhotoUploadReadiness
{
    public async Task<EmbeddedPhotoReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<string>();
        if (!EmbeddedPhotoPolicy.ValidOriginalLimit(options.Value.MaximumOriginalBytes)) reasons.Add("original-limit-invalid");
        if (!options.Value.Enabled) reasons.Add("disabled");
        if (!options.Value.UploadOnlyEnabled) reasons.Add("upload-only-disabled");
        if (string.IsNullOrWhiteSpace(options.Value.PolicyId) || options.Value.PolicyId.Length > 100) reasons.Add("policy-unavailable");
        if (!db.Database.IsNpgsql()) reasons.Add("unsupported-provider");
        else
        {
            try
            {
                if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any() || db.Database.HasPendingModelChanges())
                    reasons.Add("schema-unavailable");
                else
                {
                    // Query actual column shapes without tracking, repairing or migrating.
                    await db.PhotoUploadSessions.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoUploadAttempts.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoOriginals.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoUploadReceipts.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoIntakeRetentions.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoRevisions.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoJobs.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoProcessingAttempts.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoManifests.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoVariants.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.MediaReferenceSets.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.MediaReferenceEntries.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                    await db.PhotoOperationReceipts.AsNoTracking().Take(1).ToListAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { reasons.Add("schema-unavailable"); }
        }
        try { await storage.ListAsync(new("cmsify/media/photos/", null, 1), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { reasons.Add("storage-unavailable"); }
        return new(reasons.Count == 0, Array.AsReadOnly(reasons.Distinct(StringComparer.Ordinal).ToArray()));
    }
}
