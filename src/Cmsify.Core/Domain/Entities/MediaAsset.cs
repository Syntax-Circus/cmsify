using Cmsify.Core.Domain.Enums;

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

    public MediaBlobState BlobState { get; set; } = MediaBlobState.PendingUpload;

    public DateTimeOffset BlobStateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UploadCompletedAt { get; set; }

    public DateTimeOffset? UploadFailedAt { get; set; }

    public DateTimeOffset? BlobVerifiedAt { get; set; }

    public DateTimeOffset? MissingDetectedAt { get; set; }

    public DateTimeOffset? DeletionRequestedAt { get; set; }

    public DateTimeOffset? PurgeAfter { get; set; }

    public void TransitionBlobState(MediaBlobState target, DateTimeOffset now, DateTimeOffset? purgeAfter = null)
    {
        if (!IsAllowedTransition(BlobState, target))
        {
            throw new InvalidOperationException($"Cannot transition media blob from {BlobState} to {target}.");
        }

        BlobState = target;
        BlobStateChangedAt = now;
        switch (target)
        {
            case MediaBlobState.Available:
                UploadCompletedAt ??= now;
                BlobVerifiedAt = now;
                MissingDetectedAt = null;
                break;
            case MediaBlobState.UploadFailed:
                UploadFailedAt = now;
                break;
            case MediaBlobState.Missing:
                MissingDetectedAt = now;
                BlobVerifiedAt = now;
                break;
            case MediaBlobState.DeletePending:
                DeletionRequestedAt = now;
                PurgeAfter = purgeAfter ?? throw new ArgumentNullException(nameof(purgeAfter));
                break;
            case MediaBlobState.Deleted:
                break;
            default:
                throw new InvalidOperationException($"Transition target {target} is not supported.");
        }
    }

    private static bool IsAllowedTransition(MediaBlobState from, MediaBlobState to) => (from, to) switch
    {
        (MediaBlobState.PendingUpload, MediaBlobState.Available or MediaBlobState.UploadFailed) => true,
        (MediaBlobState.Available, MediaBlobState.Missing or MediaBlobState.DeletePending) => true,
        (MediaBlobState.Missing, MediaBlobState.Available or MediaBlobState.DeletePending) => true,
        (MediaBlobState.UploadFailed, MediaBlobState.Deleted) => true,
        (MediaBlobState.DeletePending, MediaBlobState.Deleted or MediaBlobState.Available) => true,
        _ => false
    };
}
