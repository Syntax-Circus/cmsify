namespace Cmsify.Core.EmbeddedContent;

public interface IEmbeddedContentAuthorizationService
{
    Task<bool> CanExecuteAsync(EmbeddedContentOperation operation, CancellationToken cancellationToken);
}

public enum EmbeddedContentOperationKind { TemplateRead, ItemCreate, VersionRead, VersionCreate, ReceiptRead, VersionUpdate }
public sealed record EmbeddedContentOperation(EmbeddedContentOperationKind Kind, Guid WorkspaceId,
    string ContractKey, string ContractFingerprint, Guid TemplateVersionId,
    Guid? ContentItemId = null, int? VersionNumber = null);
