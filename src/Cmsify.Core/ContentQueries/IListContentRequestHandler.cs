using SyntaxCircus.Common;

namespace Cmsify.Core.ContentQueries;

public interface IListContentRequestHandler
{
    Task<Result<ContentListOutput>> HandleAsync(ListContentRequest request, CancellationToken cancellationToken = default);
}
