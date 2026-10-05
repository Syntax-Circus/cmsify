using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using SyntaxCircus.Common;
namespace Cmsify.Core.ContentWrites;
public sealed class UpdateContentVersionRequestHandler(IContentVersionEditRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService authorization, TimeProvider clock) : IUpdateContentVersionRequestHandler
{
    public async Task<Result<UpdatedContentVersionOutput>> HandleAsync(UpdateContentVersionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated)
            return Failure(ContentVersionWriteErrors.AuthenticationRequired, "Authentication is required.", ResultErrorKind.Unauthenticated);
        if (actor.Role < UserRole.Editor)
            return Failure(ContentVersionWriteErrors.Forbidden, "Permission denied.", ResultErrorKind.Forbidden);
        if (!await authorization.CanWriteWorkspaceAsync(request.WorkspaceId, cancellationToken))
            return Failure(ContentVersionWriteErrors.NotFound, "Workspace not found.", ResultErrorKind.NotFound);
        cancellationToken.ThrowIfCancellationRequested();
        await using var session = await repository.OpenAsync(request.WorkspaceId, request.ContentItemId, request.VersionNumber, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
            return Failure(ContentVersionWriteErrors.NotFound, "Content version not found.", ResultErrorKind.NotFound);
        if (session.Snapshot.Status is not (ContentStatus.Draft or ContentStatus.Review or ContentStatus.Approved))
            return Failure(ContentVersionWriteErrors.NotEditable, "Only Draft, Review, or Approved versions can be edited", ResultErrorKind.Conflict);
        if (!request.Revision.Matches(session.Snapshot.UpdatedAt))
            return Failure(ContentVersionWriteErrors.ConcurrencyMismatch, "Concurrency mismatch", ResultErrorKind.Conflict);
        if (request.EffectiveStartAt.HasValue != request.EffectiveEndAt.HasValue)
            return Failure(ContentVersionWriteErrors.InvalidEffectiveRange,
                "Provide both effectiveStartAt and effectiveEndAt, or neither.", ResultErrorKind.Validation);
        if (request.EffectiveStartAt.HasValue && request.EffectiveStartAt >= request.EffectiveEndAt)
            return Failure(ContentVersionWriteErrors.InvalidEffectiveRange,
                "effectiveStartAt must be before effectiveEndAt.", ResultErrorKind.Validation);
        var fields = Array.AsReadOnly(request.Fields.Select(field => field is null
            ? null!
            : field with { JsonValue = field.JsonValue?.Clone() }).ToArray());
        var prepared = await session.PrepareAsync(new(request.EffectiveStartAt, request.EffectiveEndAt, fields), actor.UserId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.IsFailure) return Result<UpdatedContentVersionOutput>.Failure(prepared.Errors[0], prepared.Errors.Skip(1).ToArray());
        var committed = await session.CommitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (committed.IsFailure) return Result<UpdatedContentVersionOutput>.Failure(committed.Errors[0], committed.Errors.Skip(1).ToArray());
        // Commit precedes projection. A projection failure may follow a durable write; never retry here.
        var detail = await session.ReadDetailAsync(clock.GetUtcNow(), request.ExpandChildren, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedDetail = CopyDetail(detail);
        return Result<UpdatedContentVersionOutput>.Success(new(ContentVersionRevisionCondition.Normalize(ownedDetail.UpdatedAt), ownedDetail));
    }

    private static Result<UpdatedContentVersionOutput> Failure(string code, string message, ResultErrorKind kind)
        => Result<UpdatedContentVersionOutput>.Failure(new(code, message, kind));

    private static ContentVersionDetailOutput CopyDetail(ContentVersionDetailOutput detail) => detail with
    {
        Tags = Array.AsReadOnly(detail.Tags.ToArray()),
        Fields = Array.AsReadOnly(detail.Fields.Select(field => field with
        {
            JsonValue = field.JsonValue?.Clone(),
            Child = field.Child is { } child ? CopyDetail(child) : null
        }).ToArray())
    };
}
