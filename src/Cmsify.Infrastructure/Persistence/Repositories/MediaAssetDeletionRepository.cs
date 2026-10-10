using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Media;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class MediaAssetDeletionRepository(CmsifyDbContext db, IOptions<MediaOperationalOptions> options, TimeProvider clock)
    : IMediaAssetDeletionRepository
{
    public async Task<Result<MediaAssetDeletionOutcome>> DeleteAsync(DeleteMediaAssetRequest request, Guid? actorUserId,
        Func<CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken)
    {
        if (!await authorize(cancellationToken)) return Missing();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await EmbeddedPhotoFences.LockAssetAsync(db, request.AssetId, cancellationToken);
        if (asset is null || asset.WorkspaceId != request.WorkspaceId || asset.IsDeleted || asset.BlobState != MediaBlobState.Available) return Missing();
        if (request.ExpectedRevision is null || request.ExpectedRevision != asset.UpdatedAt.UtcTicks / TimeSpan.TicksPerMicrosecond &&
            request.ExpectedRevision != asset.UpdatedAt.UtcTicks)
            return Result<MediaAssetDeletionOutcome>.Failure(new("concurrency-mismatch", "Concurrency mismatch", ResultErrorKind.Conflict));
        var references = await EmbeddedPhotoFences.ContentReferencesAsync(db, asset.Id, false, cancellationToken);
        if (references.Count > 0)
        {
            return Result<MediaAssetDeletionOutcome>.Success(new(false, Array.AsReadOnly(references.ToArray())));
        }
        if (await EmbeddedPhotoFences.ProtectedAsync(db, asset.Id, cancellationToken))
            return Result<MediaAssetDeletionOutcome>.Failure(new(EmbeddedPhotoErrors.Retained, "Media is retained by a consumer reference.", ResultErrorKind.Conflict));
        if (!await authorize(cancellationToken)) return Missing();
        var now = clock.GetUtcNow(); var purgeAfter = now.AddDays(options.Value.RetentionDays);
        asset.TransitionBlobState(MediaBlobState.DeletePending, now, purgeAfter);
        asset.IsDeleted = true; asset.DeletedAt = now; asset.DeletedByUserId = actorUserId; asset.UpdatedAt = now;
        db.MediaDeletionIntents.Add(new MediaDeletionIntent { MediaAssetId = asset.Id, Provider = asset.StorageProvider,
            StorageKey = asset.StorageKey, Reason = "user_delete", NotBefore = purgeAfter, NextAttemptAt = purgeAfter, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return Result<MediaAssetDeletionOutcome>.Success(new(true, Array.Empty<Guid>()));
    }
    private static Result<MediaAssetDeletionOutcome> Missing() => Result<MediaAssetDeletionOutcome>.Failure(new("not-found", "Resource unavailable.", ResultErrorKind.NotFound));
}
