using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IEmbeddedContentRepository
{
    Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentAsync(CreateEmbeddedContentRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken);
    Task<Result<EmbeddedContentVersionOutput>> GetEmbeddedContentVersionAsync(GetEmbeddedContentVersionRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken);
    Task<Result<EmbeddedContentWriteOutput>> CreateEmbeddedContentVersionAsync(CreateEmbeddedContentVersionRequest request, Guid actorSubject,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken);
    Task<Result<EmbeddedContentReceiptOutput>> GetEmbeddedContentOperationReceiptAsync(GetEmbeddedContentOperationReceiptRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken);
}
