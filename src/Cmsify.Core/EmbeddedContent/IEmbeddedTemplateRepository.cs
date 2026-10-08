using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IEmbeddedTemplateRepository
{
    Task<Result<EmbeddedTemplateOutput>> EnsureAsync(EnsureEmbeddedTemplateRequest request, Guid actorSubject,
        Func<EmbeddedTemplateContractScope, CancellationToken, Task<bool>> authorizeSetup, CancellationToken cancellationToken);
    Task<Result<EmbeddedTemplateOutput>> GetAsync(GetEmbeddedTemplateRequest request,
        Func<EmbeddedContentOperation, CancellationToken, Task<bool>> authorizeLoaded, CancellationToken cancellationToken);
}
