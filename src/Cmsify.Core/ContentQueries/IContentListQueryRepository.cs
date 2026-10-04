namespace Cmsify.Core.ContentQueries;

public interface IContentListQueryRepository
{
    Task<ContentListPage> ListItemsAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default);
    Task<ContentListPage> ListResolvedAsync(ContentListCriteria criteria, CancellationToken cancellationToken = default);
}
