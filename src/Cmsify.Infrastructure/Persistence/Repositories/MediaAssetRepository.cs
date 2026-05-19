using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class MediaAssetRepository : IMediaAssetRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public MediaAssetRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<MediaAssetDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.MediaAssets.AsNoTracking().ScopeToActorWorkspace(currentActor).FirstOrDefaultAsync(asset => asset.Id == id, ct))?.ToDto();

    public Task<PagedResult<MediaAssetDto>> ListByWorkspaceAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default) =>
        dbContext.MediaAssets.AsNoTracking()
            .Where(asset => asset.WorkspaceId == workspaceId)
            .ScopeToActorWorkspace(currentActor)
            .OrderBy(asset => asset.FileName)
            .ToPagedResultAsync(page, asset => asset.ToDto(), ct);

    public async Task<MediaAssetDto> CreateAsync(CreateMediaAssetCommand command, CancellationToken ct = default)
    {
        var entity = new MediaAsset
        {
            WorkspaceId = command.WorkspaceId,
            FileName = command.FileName,
            MimeType = command.MimeType,
            SizeBytes = command.SizeBytes,
            StorageKey = command.StorageKey,
            StorageProvider = command.StorageProvider,
            AltText = command.AltText
        };
        dbContext.MediaAssets.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<MediaAssetDto> UpdateAsync(UpdateMediaAssetCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.MediaAssets.ScopeToActorWorkspace(currentActor).FirstAsync(asset => asset.Id == command.Id, ct);
        entity.FileName = command.FileName;
        entity.AltText = command.AltText;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task SoftDeleteAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var entity = await dbContext.MediaAssets.ScopeToActorWorkspace(currentActor).FirstAsync(asset => asset.Id == id, ct);
        entity.SoftDelete(actorUserId);
        await dbContext.SaveChangesAsync(ct);
    }
}
