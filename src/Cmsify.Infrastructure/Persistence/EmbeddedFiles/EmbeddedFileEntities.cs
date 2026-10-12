using Cmsify.Core.EmbeddedFiles;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

public sealed class FileUploadSessionRecord
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AssetId { get; set; }
    public Guid ActorId { get; set; }
    public Guid OperationKey { get; set; }
    public string ConsumerNamespace { get; set; }="";
    public string Fingerprint { get; set; }="";
    public string DisplayFileName { get; set; }="";
    public long DeclaredLength { get; set; }
    public string ExpectedSha256 { get; set; }="";
    public EmbeddedFileUploadState State { get; set; }
    public Guid? WriteOperationKey { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class FileUploadAttemptRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string StorageProvider { get; set; }="";
    public string StorageKey { get; set; }="";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class FileUploadReceiptRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AssetId { get; set; }
    public string ConsumerNamespace { get; set; }="";
    public long Length { get; set; }
    public string SourceSha256 { get; set; }="";
    public string StorageProvider { get; set; }="";
    public string StorageKey { get; set; }="";
    public DateTimeOffset CreatedAt { get; set; }
}
