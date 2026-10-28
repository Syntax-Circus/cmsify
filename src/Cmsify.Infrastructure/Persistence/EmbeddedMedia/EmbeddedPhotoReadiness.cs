using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

/// <summary>Read-only prerequisite check. Does not qualify native processing or test storage writes.</summary>
public sealed class EmbeddedPhotoReadiness(CmsifyDbContext db, IStorageProvider storage, IEmbeddedPhotoProcessorCapability capability,
    IOptions<EmbeddedPhotoOptions> options) : IEmbeddedPhotoReadiness
{
    public async Task<EmbeddedPhotoReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<string>();
        if (!EmbeddedPhotoPolicy.ValidOriginalLimit(options.Value.MaximumOriginalBytes)) reasons.Add("original-limit-invalid");
        if (!options.Value.Enabled) reasons.Add("disabled");
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
        try
        {
            var available = await capability.GetAsync(options.Value.PolicyId, cancellationToken);
            if (!available.Available || !available.Qualified || string.IsNullOrWhiteSpace(available.ProcessorVersion) ||
                available.ProcessorVersion.Length > 100 || string.IsNullOrWhiteSpace(available.SandboxIdentity)) reasons.Add("capability-unavailable");
            else if (available.PolicyId != options.Value.PolicyId) reasons.Add("policy-mismatch");
            if (options.Value.PngNormalizationEnabled)
            {
                var png = await capability.GetAsync(EmbeddedPhotoPolicy.PngNormalizationPolicyId, cancellationToken);
                if (!png.Available || !png.Qualified || png.PolicyId != EmbeddedPhotoPolicy.PngNormalizationPolicyId ||
                    string.IsNullOrWhiteSpace(png.ProcessorVersion) || png.ProcessorVersion.Length > 100 || string.IsNullOrWhiteSpace(png.SandboxIdentity))
                    reasons.Add("png-capability-unavailable");
            }
            if (options.Value.JpegSdrBaseEnabled)
            {
                var jpeg = await capability.GetAsync(EmbeddedPhotoPolicy.JpegSdrBasePolicyId, cancellationToken);
                if (!jpeg.Available || !jpeg.Qualified || jpeg.PolicyId != EmbeddedPhotoPolicy.JpegSdrBasePolicyId ||
                    string.IsNullOrWhiteSpace(jpeg.ProcessorVersion) || jpeg.ProcessorVersion.Length > 100 || string.IsNullOrWhiteSpace(jpeg.SandboxIdentity))
                    reasons.Add("jpeg-sdr-capability-unavailable");
            }
            if (options.Value.PngCicpSdrEnabled)
            {
                var cicp = await capability.GetAsync(EmbeddedPhotoPolicy.PngCicpSdrPolicyId, cancellationToken);
                if (!cicp.Available || !cicp.Qualified || cicp.PolicyId != EmbeddedPhotoPolicy.PngCicpSdrPolicyId ||
                    string.IsNullOrWhiteSpace(cicp.ProcessorVersion) || cicp.ProcessorVersion.Length > 100 || string.IsNullOrWhiteSpace(cicp.SandboxIdentity))
                    reasons.Add("png-cicp-capability-unavailable");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { reasons.Add("capability-unavailable"); }
        return new(reasons.Count == 0, Array.AsReadOnly(reasons.Distinct(StringComparer.Ordinal).ToArray()));
    }
}
