using Cmsify.Core.EmbeddedMedia;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

public sealed class PhotoUploadSessionRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid AssetId { get; set; }
    public Guid OperationKey { get; set; }
    public string DisplayFileName { get; set; } = null!;
    public long DeclaredLength { get; set; }
    public string PolicyId { get; set; } = null!;
    public string ProcessorVersion { get; set; } = null!;
    public EmbeddedPhotoUploadState State { get; set; }
    public int AttemptGeneration { get; set; }
    public Guid? LeaseToken { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? WriteOperationKey { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed class PhotoUploadAttemptRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Generation { get; set; }
    public Guid Token { get; set; }
    public Guid OwnerId { get; set; }
    public string StorageProvider { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetiredAt { get; set; }
}
public sealed class PhotoOriginalRecord
{
    public Guid AssetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public string StorageProvider { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public long Length { get; set; }
    public string SourceSha256 { get; set; } = null!;
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PhotoUploadReceiptRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AssetId { get; set; }
    public Guid OperationKey { get; set; }
    public string SourceSha256 { get; set; } = null!;
    public long Length { get; set; }
    public int InitialRevision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PhotoRevisionRecord
{
    public Guid AssetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Revision { get; set; }
    public Guid OperationKey { get; set; }
    public string SourceSha256 { get; set; } = null!;
    public string PolicyId { get; set; } = null!;
    public string ProcessorVersion { get; set; } = null!;
    public int QuarterTurnsClockwise { get; set; }
    public EmbeddedPhotoState State { get; set; }
    public int RetryGeneration { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PhotoJobRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AssetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Revision { get; set; }
    public string SourceSha256 { get; set; } = null!;
    public string PolicyId { get; set; } = null!;
    public string ProcessorVersion { get; set; } = null!;
    public EmbeddedPhotoState State { get; set; }
    public int RetryGeneration { get; set; }
    public int AutomaticAttemptCount { get; set; }
    public Guid? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PhotoProcessingAttemptRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid Token { get; set; }
    public int RetryGeneration { get; set; }
    public string VariantKey { get; set; } = null!;
    public string StorageProvider { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetiredAt { get; set; }
}
public sealed class PhotoManifestRecord
{
    public Guid AssetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Revision { get; set; }
    public string ManifestSha256 { get; set; } = null!;
    public string SourceSha256 { get; set; } = null!;
    public string PolicyId { get; set; } = null!;
    public string ProcessorVersion { get; set; } = null!;
    public int QuarterTurnsClockwise { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PhotoVariantRecord
{
    public Guid AssetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Revision { get; set; }
    public string VariantKey { get; set; } = null!;
    public string ContentType { get; set; } = EmbeddedPhotoPolicy.WebpContentType;
    public int Width { get; set; }
    public int Height { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = null!;
    public string StorageProvider { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
}
public sealed class MediaReferenceSetRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string ConsumerNamespace { get; set; } = null!;
    public Guid ReferenceKey { get; set; }
    public Guid OperationKey { get; set; }
    public string InputFingerprint { get; set; } = null!;
    public int ProtectionRevision { get; set; } = 1;
    public EmbeddedMediaReferenceState State { get; set; }
    public Guid? ReleaseOperationKey { get; set; }
    public Guid? ReleaseReceiptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }
}
public sealed class MediaReferenceEntryRecord
{
    public Guid ReferenceSetId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Ordinal { get; set; }
    public Guid AssetId { get; set; }
    public int Revision { get; set; }
    public string ManifestSha256 { get; set; } = null!;
}
public sealed class PhotoOperationReceiptRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public EmbeddedPhotoOperationKind Kind { get; set; }
    public Guid OperationKey { get; set; }
    public string InputFingerprint { get; set; } = null!;
    public string OutputJson { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
