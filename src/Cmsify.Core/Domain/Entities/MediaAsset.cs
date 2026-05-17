namespace Cmsify.Core.Domain.Entities;

public sealed class MediaAsset : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string FileName { get; set; }

    public required string MimeType { get; set; }

    public long SizeBytes { get; set; }

    public required string StorageKey { get; set; }

    public required string StorageProvider { get; set; }

    public string? AltText { get; set; }

    public Guid? CreatedByUserId { get; set; }
}
