using SyntaxCircus.Common;
namespace Cmsify.Core.ContentWrites;
/// <summary>An operation-owned edit: prepare never saves; commit persists once; detail follows commit.</summary>
public interface IContentVersionEditSession : IAsyncDisposable
{
    ContentVersionEditSnapshot Snapshot { get; }
    Task<Result> PrepareAsync(ContentVersionEditValues values, Guid? actorUserId, CancellationToken cancellationToken);
    Task<Result> CommitAsync(CancellationToken cancellationToken);
    Task<ContentVersionDetailOutput> ReadDetailAsync(DateTimeOffset asOf, bool expandChildren, CancellationToken cancellationToken);
}
