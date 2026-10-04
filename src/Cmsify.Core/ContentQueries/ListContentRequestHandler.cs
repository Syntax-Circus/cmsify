using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using SyntaxCircus.Common;

namespace Cmsify.Core.ContentQueries;

public sealed class ListContentRequestHandler : IListContentRequestHandler
{
    private readonly IContentListQueryRepository _repository;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;
    private readonly TimeProvider _clock;

    public ListContentRequestHandler(IContentListQueryRepository repository, ICurrentActor actor,
        IWorkspaceAuthorizationService authorization, TimeProvider clock)
    {
        _repository = repository;
        _actor = actor;
        _authorization = authorization;
        _clock = clock;
    }

    public async Task<Result<ContentListOutput>> HandleAsync(ListContentRequest request, CancellationToken cancellationToken = default)
    {
        if (!_actor.IsAuthenticated)
            return Result<ContentListOutput>.Failure(new("authentication-required", "Authentication is required.", ResultErrorKind.Unauthenticated));
        if (_actor.Role < UserRole.Reader)
            return Result<ContentListOutput>.Failure(new("forbidden", "Permission denied.", ResultErrorKind.Forbidden));
        if (!await _authorization.CanReadWorkspaceAsync(request.WorkspaceId, cancellationToken))
            return Result<ContentListOutput>.Failure(new("not-found", "Workspace not found.", ResultErrorKind.NotFound));
        if (request.Page < 1 || request.PageSize < 1 || request.PageSize > ContentListRules.MaxPageSize)
            return Result<ContentListOutput>.Failure(new("validation-failed", "Invalid pagination.", ResultErrorKind.Validation));

        var offset = ((long)request.Page - 1) * request.PageSize;
        var evaluationTime = request.Resolve && request.AsOf.HasValue ? request.AsOf.Value : _clock.GetUtcNow();
        var criteria = new ContentListCriteria(request, evaluationTime, offset > int.MaxValue ? null : (int)offset);
        var page = request.Resolve
            ? await _repository.ListResolvedAsync(criteria, cancellationToken)
            : await _repository.ListItemsAsync(criteria, cancellationToken);
        var items = criteria.Offset.HasValue
            ? page.Items.Select(CopyItem).ToArray()
            : [];
        return Result<ContentListOutput>.Success(new(Array.AsReadOnly(items), page.TotalCount, request.Page, request.PageSize));
    }

    private static ContentListItemOutput CopyItem(ContentListItemOutput item) => item with
    {
        Tags = Array.AsReadOnly(item.Tags.ToArray()),
        CurrentlyServingVersion = item.CurrentlyServingVersion is { } version
            ? version with { Tags = Array.AsReadOnly(version.Tags.ToArray()) }
            : null
    };
}
