namespace Cmsify.Core.ContentWrites;
public interface IContentVersionEditRepository
{
    Task<IContentVersionEditSession?> OpenAsync(Guid workspaceId, Guid itemId, int versionNumber, CancellationToken cancellationToken);
}
