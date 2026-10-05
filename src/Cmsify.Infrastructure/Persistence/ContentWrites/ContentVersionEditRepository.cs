using Cmsify.Core.ContentWrites;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.ContentWrites;

/// <summary>Creates an isolated edit using the host's scoped provider and audit options.</summary>
public sealed class ContentVersionEditRepository(DbContextOptions<CmsifyDbContext> options,
    IContentValidator validator, IContentSearchVectorBuilder searchVectorBuilder, TimeProvider clock)
    : IContentVersionEditRepository
{
    public async Task<IContentVersionEditSession?> OpenAsync(Guid workspaceId, Guid itemId, int versionNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new CmsifyDbContext(options);
        try
        {
            var itemExists = await context.ContentItems.AnyAsync(item => item.Id == itemId
                && item.WorkspaceId == workspaceId && !item.IsDeleted, cancellationToken);
            var version = itemExists
                ? await context.ContentVersions.Include(version => version.FieldValues)
                    .FirstOrDefaultAsync(version => version.ContentItemId == itemId && version.WorkspaceId == workspaceId
                        && version.VersionNumber == versionNumber, cancellationToken)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (version is not null)
                return new ContentVersionEditSession(context, version, validator, searchVectorBuilder, clock);
            await context.DisposeAsync();
            return null;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
