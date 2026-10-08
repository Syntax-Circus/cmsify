namespace Cmsify.Core.EmbeddedContent;

public interface IEmbeddedContentAuthorizationService
{
    Task<bool> CanExecuteAsync(EmbeddedContentOperation operation, CancellationToken cancellationToken);
}

public enum EmbeddedContentOperationKind { TemplateRead, ItemCreate, VersionRead, VersionCreate, ReceiptRead, VersionUpdate }
/// <summary>VersionNumber is the immutable requested source for VersionCreate, including response/replay;
/// other version operations identify their selected version. Allocated targets are returned in write outputs.</summary>
public sealed record EmbeddedContentOperation(EmbeddedContentOperationKind Kind, Guid WorkspaceId,
    string ContractKey, string ContractFingerprint, Guid TemplateVersionId,
    Guid? ContentItemId = null, int? VersionNumber = null);
