namespace Cmsify.Core.ContentWrites;

public interface IContentVersionResourceGuard
{
    Task<bool> CanEditAsync(ContentVersionEditSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class UnrestrictedContentVersionResourceGuard : IContentVersionResourceGuard
{
    public Task<bool> CanEditAsync(ContentVersionEditSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
