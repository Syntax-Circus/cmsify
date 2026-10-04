using Cmsify.Core.ContentQueries;

namespace Cmsify.Infrastructure.Persistence.ContentQueries;

public sealed class UnsupportedContentListQueryRepository : IContentListQueryRepository
{
    public Task<ContentListPage> ListItemsAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This Cmsify database provider does not support content list queries.");
    public Task<ContentListPage> ListResolvedAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This Cmsify database provider does not support content list queries.");
}
