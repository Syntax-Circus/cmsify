namespace Cmsify.Core.EmbeddedMedia;

public enum EmbeddedPhotoState { Queued, Processing, Ready, Rejected, Failed, AwaitingProcessing = 5 }
public enum EmbeddedPhotoUploadState { Pending, Active, Completed, Abandoned, Failed }
public enum EmbeddedMediaReferenceState { Protected, Released }
public enum EmbeddedMediaRetentionDecision { ReleaseAllowed, Retain, Unknown }
public enum EmbeddedPhotoReadKind { PrivatePreview, ConsumerReference }
public enum EmbeddedPhotoOperationKind { BeginUpload, WriteUpload, ReadUploadOutcome, ReadStatus, CreateRevision, Retry, ReadManifest, Protect, ReadReferenceOutcome, Release, ReadOriginal, ReadVariant, Process }
public sealed record EmbeddedPhotoInstructions(int QuarterTurnsClockwise);
public sealed record EmbeddedPhotoManifestIdentity(Guid AssetId, int Revision, string ManifestSha256);
public sealed record EmbeddedPhotoVariantDescriptor(string VariantKey, string ContentType, int Width, int Height, long Length, string Sha256);
// Dimensions are after orientation and before manual quarter-turns; bit depth is the selected SDR decode.
public sealed record EmbeddedPhotoSourceMetadata(string Format, int Width, int Height, int BitDepth, string ColorDescription, bool HasAlpha, bool UsedSdrBase);
public sealed record EmbeddedPhotoUploadReceipt(Guid ReceiptId, string SourceSha256, long Length, int InitialRevision);
public sealed record EmbeddedPhotoUploadOutcome(Guid WorkspaceId, Guid SessionId, Guid AssetId, Guid OperationKey, EmbeddedPhotoUploadState State, EmbeddedPhotoUploadReceipt? Receipt);
public sealed record EmbeddedPhotoStatus(Guid WorkspaceId, Guid AssetId, int Revision, string PolicyId, string? ProcessorVersion, int RetryGeneration, EmbeddedPhotoState State, string? FailureCode,
    EmbeddedPhotoManifestIdentity? Manifest = null);

public sealed record EmbeddedPhotoManifest
{
    public Guid WorkspaceId { get; }
    public EmbeddedPhotoManifestIdentity Identity { get; }
    public string SourceSha256 { get; }
    public string PolicyId { get; }
    public string ProcessorVersion { get; }
    public EmbeddedPhotoInstructions Instructions { get; }
    public IReadOnlyList<EmbeddedPhotoVariantDescriptor> Variants { get; }
    public EmbeddedPhotoManifest(Guid workspaceId, EmbeddedPhotoManifestIdentity identity, string sourceSha256,
        string policyId, string processorVersion, EmbeddedPhotoInstructions instructions, IReadOnlyList<EmbeddedPhotoVariantDescriptor> variants)
    {
        WorkspaceId = workspaceId; Identity = identity; SourceSha256 = sourceSha256;
        PolicyId = policyId; ProcessorVersion = processorVersion; Instructions = instructions;
        Variants = Array.AsReadOnly(variants.ToArray());
    }
}

public sealed record EmbeddedMediaReferenceReceipt(Guid ReceiptId, Guid OperationKey);
public sealed record EmbeddedMediaReferenceOutcome
{
    public Guid WorkspaceId { get; }
    public string ConsumerNamespace { get; }
    public Guid ReferenceKey { get; }
    public int ProtectionRevision { get; }
    public EmbeddedMediaReferenceState State { get; }
    public IReadOnlyList<EmbeddedPhotoManifestIdentity> Manifests { get; }
    public EmbeddedMediaReferenceReceipt ProtectionReceipt { get; }
    public EmbeddedMediaReferenceReceipt? ReleaseReceipt { get; }
    public EmbeddedMediaReferenceOutcome(Guid workspaceId, string consumerNamespace, Guid referenceKey, int protectionRevision,
        EmbeddedMediaReferenceState state, IReadOnlyList<EmbeddedPhotoManifestIdentity> manifests,
        EmbeddedMediaReferenceReceipt protectionReceipt, EmbeddedMediaReferenceReceipt? releaseReceipt)
    {
        WorkspaceId = workspaceId; ConsumerNamespace = consumerNamespace; ReferenceKey = referenceKey;
        ProtectionRevision = protectionRevision; State = state; Manifests = Array.AsReadOnly(manifests.ToArray());
        ProtectionReceipt = protectionReceipt; ReleaseReceipt = releaseReceipt;
    }
}

public sealed record EmbeddedPhotoReadAuthority(EmbeddedPhotoReadKind Kind, string? ConsumerNamespace = null, Guid? ReferenceKey = null, int? ProtectionRevision = null);
public sealed record EmbeddedPhotoReadOutput(long Length, string Sha256, string ContentType);
public sealed record BeginEmbeddedPhotoUploadRequest(Guid WorkspaceId, Guid AssetId, Guid OperationKey, string DisplayFileName, long DeclaredLength, string PolicyId)
{
    public string? ExpectedSourceSha256 { get; init; }
}
public sealed record WriteEmbeddedPhotoUploadRequest(Guid WorkspaceId, Guid SessionId, Guid OperationKey, IEmbeddedPhotoByteSource Source);
public sealed record GetEmbeddedPhotoUploadOutcomeRequest(Guid WorkspaceId, Guid SessionId);
public sealed record GetEmbeddedPhotoUploadIntentRequest(Guid WorkspaceId, Guid AssetId, Guid OperationKey);
public sealed record GetEmbeddedPhotoRequest(Guid WorkspaceId, Guid AssetId, int Revision);
public sealed record CreateEmbeddedPhotoRevisionRequest(Guid WorkspaceId, Guid AssetId, Guid OperationKey, int ExpectedLatestRevision, EmbeddedPhotoInstructions Instructions, string PolicyId);
public sealed record RetryEmbeddedPhotoProcessingRequest(Guid WorkspaceId, Guid AssetId, int Revision, Guid OperationKey, int ExpectedRetryGeneration);
public sealed record GetEmbeddedPhotoManifestRequest(Guid WorkspaceId, Guid AssetId, int Revision, string ManifestSha256);
public sealed record ProtectEmbeddedMediaReferencesRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, Guid OperationKey, IReadOnlyList<EmbeddedPhotoManifestIdentity> Manifests);
public sealed record GetEmbeddedMediaReferenceOutcomeRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey);
public sealed record ReleaseEmbeddedMediaReferencesRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, Guid OperationKey, int ExpectedProtectionRevision);
public sealed record ReadEmbeddedPhotoOriginalRequest(Guid WorkspaceId, Guid AssetId, IEmbeddedPhotoByteSink Sink);
public sealed record ReadEmbeddedPhotoVariantRequest(Guid WorkspaceId, EmbeddedPhotoManifestIdentity Manifest, string VariantKey, EmbeddedPhotoReadAuthority Authority, IEmbeddedPhotoByteSink Sink);
public sealed record EmbeddedPhotoOperation(EmbeddedPhotoOperationKind Kind, Guid WorkspaceId, Guid? AssetId = null, int? Revision = null, string? ConsumerNamespace = null, Guid? ReferenceKey = null);
public sealed record EmbeddedMediaRetentionRequest(Guid WorkspaceId, string ConsumerNamespace, Guid ReferenceKey, int ProtectionRevision, IReadOnlyList<EmbeddedPhotoManifestIdentity> Manifests);
public sealed record EmbeddedPhotoProcessingInput(IEmbeddedPhotoByteSource Original, string SourceSha256, long Length, EmbeddedPhotoInstructions Instructions, EmbeddedPhotoPolicyIdentity Policy);
public sealed record EmbeddedPhotoCandidateVariant(EmbeddedPhotoVariantDescriptor Descriptor, IEmbeddedPhotoByteSource Source);
public sealed record EmbeddedPhotoCapability(bool Available, bool Qualified, string PolicyId, string ProcessorVersion, string SandboxIdentity, string? FailureCode);
public sealed record EmbeddedPhotoReadinessResult(bool Ready, IReadOnlyList<string> Reasons);

public sealed class EmbeddedPhotoCandidate(EmbeddedPhotoSourceMetadata metadata, IReadOnlyList<EmbeddedPhotoCandidateVariant> variants,
    Func<ValueTask>? cleanup = null) : IAsyncDisposable
{
    public EmbeddedPhotoSourceMetadata Metadata { get; } = metadata;
    public IReadOnlyList<EmbeddedPhotoCandidateVariant> Variants { get; } = Array.AsReadOnly(variants.ToArray());
    private bool _disposed;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (cleanup is not null) await cleanup();
    }
}
