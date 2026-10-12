namespace Cmsify.Core.EmbeddedFiles;

public enum EmbeddedFileUploadState { Pending, Active, Interrupted, Completed, Failed, Expired }
public enum EmbeddedFileReferenceState { Protected, Released }
public enum EmbeddedFileOperationKind { BeginUpload, WriteUpload, ReadUploadIntent, ReadUploadOutcome, ReadFile, Protect, ReadReferenceOutcome, Release }
public sealed record EmbeddedFileReceipt(Guid ReceiptId, Guid WorkspaceId, Guid AssetId, string ConsumerNamespace, long Length, string SourceSha256, string MediaType);
public sealed record EmbeddedFileUploadOutcome(Guid WorkspaceId, Guid AssetId, Guid SessionId, Guid OperationKey, EmbeddedFileUploadState State, EmbeddedFileReceipt? Receipt, string? FailureCode);
public sealed record EmbeddedFileIdentity(Guid AssetId, Guid ReceiptId, long Length, string SourceSha256);
public sealed record EmbeddedFileReferenceOutcome(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, Guid OperationKey, int ProtectionRevision, EmbeddedFileReferenceState State, IReadOnlyList<EmbeddedFileIdentity> Files);
public sealed record EmbeddedFileOperation(EmbeddedFileOperationKind Kind, Guid WorkspaceId, Guid? AssetId = null, Guid? SessionId = null, Guid? ReceiptId = null, string? ConsumerNamespace = null, Guid? ReferenceKey = null, Guid? ActorId = null);
public sealed record EmbeddedFileRetentionRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, int ProtectionRevision, IReadOnlyList<EmbeddedFileIdentity> Files);
public sealed record EmbeddedFileRetentionDecision(bool AllowRelease, string? Reason = null);
public sealed record RegisteredEmbeddedFileRetentionGuard(string ConsumerNamespace, IEmbeddedFileRetentionGuard Guard);
public sealed record BeginEmbeddedFileUploadRequest(Guid WorkspaceId, Guid AssetId, Guid OperationKey, string ConsumerNamespace, string DisplayFileName, long DeclaredLength, string ExpectedSourceSha256, string PolicyId);
public sealed record WriteEmbeddedFileUploadRequest(Guid WorkspaceId, Guid SessionId, Guid OperationKey, IEmbeddedFileByteSource Source);
public sealed record GetEmbeddedFileUploadIntentRequest(Guid WorkspaceId, string ConsumerNamespace, Guid AssetId, Guid BeginOperationKey);
public sealed record GetEmbeddedFileUploadOutcomeRequest(Guid WorkspaceId, Guid SessionId);
public sealed record ReadEmbeddedFileRequest(Guid WorkspaceId, string ConsumerNamespace, EmbeddedFileIdentity File, IEmbeddedFileByteSink Sink);
public sealed record ProtectEmbeddedFileReferencesRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, Guid OperationKey, IReadOnlyList<EmbeddedFileIdentity> Files);
public sealed record GetEmbeddedFileReferenceOutcomeRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey);
public sealed record ReleaseEmbeddedFileReferencesRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, Guid OperationKey, int ExpectedProtectionRevision);
