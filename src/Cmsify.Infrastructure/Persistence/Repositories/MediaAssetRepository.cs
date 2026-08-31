using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class MediaAssetRepository : IMediaAssetRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IStorageProvider storage;
    private readonly MediaOperationalOptions mediaOperations;
    private readonly string providerName;
    private readonly TimeProvider timeProvider;

    public MediaAssetRepository(
        CmsifyDbContext dbContext,
        ICurrentActor currentActor,
        IStorageProvider storage,
        IOptions<MediaOperationalOptions> mediaOperations,
        IConfiguration configuration,
        TimeProvider? timeProvider = null)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.storage = storage;
        this.mediaOperations = mediaOperations.Value;
        providerName = (configuration["Storage:Provider"] ?? "local").ToLowerInvariant();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MediaAssetDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.MediaAssets.AsNoTracking()
            .Where(asset => asset.BlobState == MediaBlobState.Available)
            .ScopeToActorWorkspace(currentActor)
            .FirstOrDefaultAsync(asset => asset.Id == id, ct))?.ToDto();

    public Task<PagedResult<MediaAssetDto>> ListByWorkspaceAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default) =>
        dbContext.MediaAssets.AsNoTracking()
            .Where(asset => asset.WorkspaceId == workspaceId && asset.BlobState == MediaBlobState.Available)
            .ScopeToActorWorkspace(currentActor)
            .OrderBy(asset => asset.FileName)
            .ToPagedResultAsync(page, asset => asset.ToDto(), ct);

    public async Task<MediaAssetDto> CreateAsync(CreateMediaAssetCommand command, CancellationToken ct = default)
    {
        if (!string.Equals(command.StorageProvider, providerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Media storage provider does not match the configured provider.");
        }

        var metadata = await storage.GetMetadataAsync(command.StorageKey, ct);
        if (metadata is null || !string.Equals(metadata.Key, command.StorageKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Media blob could not be verified.");
        }

        var now = timeProvider.GetUtcNow();
        var entity = new MediaAsset
        {
            WorkspaceId = command.WorkspaceId,
            FileName = command.FileName,
            MimeType = command.MimeType,
            SizeBytes = metadata.SizeBytes,
            StorageKey = command.StorageKey,
            StorageProvider = providerName,
            AltText = command.AltText,
            BlobState = MediaBlobState.PendingUpload,
            BlobStateChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        entity.TransitionBlobState(MediaBlobState.Available, now);
        dbContext.MediaAssets.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<MediaAssetDto> UpdateAsync(UpdateMediaAssetCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.MediaAssets
            .Where(asset => asset.BlobState == MediaBlobState.Available)
            .ScopeToActorWorkspace(currentActor)
            .FirstAsync(asset => asset.Id == command.Id, ct);
        entity.FileName = command.FileName;
        entity.AltText = command.AltText;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.MediaAssets
            .Where(asset => asset.BlobState == MediaBlobState.Available)
            .ScopeToActorWorkspace(currentActor)
            .FirstAsync(asset => asset.Id == id, ct);
        var now = timeProvider.GetUtcNow();
        var purgeAfter = now.AddDays(mediaOperations.RetentionDays);
        entity.TransitionBlobState(MediaBlobState.DeletePending, now, purgeAfter);
        entity.IsDeleted = true;
        entity.DeletedAt = now;
        entity.DeletedByUserId = actorUserId;
        entity.UpdatedAt = now;
        dbContext.MediaDeletionIntents.Add(new MediaDeletionIntent
        {
            MediaAssetId = entity.Id,
            Provider = entity.StorageProvider,
            StorageKey = entity.StorageKey,
            Reason = "user_delete",
            NotBefore = purgeAfter,
            NextAttemptAt = purgeAfter,
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(ct);
    }
}
