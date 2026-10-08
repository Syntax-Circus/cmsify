using Cmsify.Core.ContentWrites;

namespace Cmsify.Core.EmbeddedContent;

public enum EmbeddedWriteKind { ItemCreate, VersionCreate }
public sealed record CreateEmbeddedContentRequest(Guid WorkspaceId, Guid TemplateVersionId, string ContractFingerprint,
    Guid ContentItemId, Guid OperationKey, IReadOnlyList<ContentVersionFieldInput> Fields);
public sealed record GetEmbeddedContentVersionRequest(Guid WorkspaceId, Guid ContentItemId, int VersionNumber,
    Guid TemplateVersionId, string ContractFingerprint, ContentVersionRevisionCondition? Revision = null);
public sealed record CreateEmbeddedContentVersionRequest(Guid WorkspaceId, Guid ContentItemId, int SourceVersionNumber,
    ContentVersionRevisionCondition SourceRevision, Guid TemplateVersionId, string ContractFingerprint,
    Guid OperationKey, IReadOnlyList<ContentVersionFieldInput> Fields);
public sealed record GetEmbeddedContentOperationReceiptRequest(Guid WorkspaceId, Guid ContentItemId,
    EmbeddedWriteKind Kind, Guid OperationKey, string ContractFingerprint);
public sealed record EmbeddedContentVersionOutput(string ContractFingerprint, long Revision, ContentVersionDetailOutput Version);
public sealed record EmbeddedContentReceiptOutput(Guid WorkspaceId, Guid ContentItemId, EmbeddedWriteKind Kind,
    Guid OperationKey, int VersionNumber, long CommittedRevision, Guid ActorSubject, DateTimeOffset CommittedAt);
public sealed record EmbeddedContentWriteOutput(EmbeddedContentReceiptOutput Receipt, EmbeddedContentVersionOutput Version);
