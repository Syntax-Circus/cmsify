namespace Cmsify.Core.EmbeddedContent;

public sealed class DenyEmbeddedContentAuthorizationService : IEmbeddedContentAuthorizationService
{
    public Task<bool> CanExecuteAsync(EmbeddedContentOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

public sealed class DenyEmbeddedTemplateSetupAuthorizationService : IEmbeddedTemplateSetupAuthorizationService
{
    public Task<bool> CanEnsureAsync(EmbeddedTemplateContractScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
