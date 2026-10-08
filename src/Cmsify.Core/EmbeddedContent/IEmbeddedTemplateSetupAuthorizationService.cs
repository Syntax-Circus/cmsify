namespace Cmsify.Core.EmbeddedContent;

public interface IEmbeddedTemplateSetupAuthorizationService
{
    Task<bool> CanEnsureAsync(EmbeddedTemplateContractScope scope, CancellationToken cancellationToken);
}

public sealed record EmbeddedTemplateContractScope(Guid WorkspaceId, string ContractKey, string Fingerprint);
