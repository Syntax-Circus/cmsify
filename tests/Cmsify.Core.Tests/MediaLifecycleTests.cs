using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Tests;

public sealed class MediaLifecycleTests
{
    [Theory]
    [InlineData(MediaBlobState.PendingUpload, MediaBlobState.Available)]
    [InlineData(MediaBlobState.PendingUpload, MediaBlobState.UploadFailed)]
    [InlineData(MediaBlobState.Available, MediaBlobState.Missing)]
    [InlineData(MediaBlobState.Available, MediaBlobState.DeletePending)]
    [InlineData(MediaBlobState.Missing, MediaBlobState.Available)]
    [InlineData(MediaBlobState.Missing, MediaBlobState.DeletePending)]
    [InlineData(MediaBlobState.UploadFailed, MediaBlobState.Deleted)]
    [InlineData(MediaBlobState.DeletePending, MediaBlobState.Deleted)]
    [InlineData(MediaBlobState.DeletePending, MediaBlobState.Available)]
    public void TransitionBlobState_AllowsDocumentedTransitions(MediaBlobState from, MediaBlobState to)
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var asset = CreateAsset(from);

        asset.TransitionBlobState(to, now, to == MediaBlobState.DeletePending ? now.AddDays(30) : null);

        asset.BlobState.ShouldBe(to);
        asset.BlobStateChangedAt.ShouldBe(now);
    }

    [Theory]
    [InlineData(MediaBlobState.Available, MediaBlobState.UploadFailed)]
    [InlineData(MediaBlobState.UploadFailed, MediaBlobState.Available)]
    [InlineData(MediaBlobState.Deleted, MediaBlobState.Available)]
    [InlineData(MediaBlobState.PendingUpload, MediaBlobState.Deleted)]
    public void TransitionBlobState_RejectsInvalidTransitions(MediaBlobState from, MediaBlobState to)
    {
        var asset = CreateAsset(from);

        Should.Throw<InvalidOperationException>(() => asset.TransitionBlobState(to, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TransitionBlobState_RecordsStateSpecificTimestamps()
    {
        var uploadedAt = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var missingAt = uploadedAt.AddMinutes(1);
        var reappearedAt = uploadedAt.AddMinutes(2);
        var deleteAt = uploadedAt.AddMinutes(3);
        var purgeAfter = deleteAt.AddDays(30);
        var asset = CreateAsset(MediaBlobState.PendingUpload);

        asset.TransitionBlobState(MediaBlobState.Available, uploadedAt);
        asset.TransitionBlobState(MediaBlobState.Missing, missingAt);
        asset.TransitionBlobState(MediaBlobState.Available, reappearedAt);
        asset.TransitionBlobState(MediaBlobState.DeletePending, deleteAt, purgeAfter);

        asset.UploadCompletedAt.ShouldBe(uploadedAt);
        asset.BlobVerifiedAt.ShouldBe(reappearedAt);
        asset.MissingDetectedAt.ShouldBeNull();
        asset.DeletionRequestedAt.ShouldBe(deleteAt);
        asset.PurgeAfter.ShouldBe(purgeAfter);
    }

    private static MediaAsset CreateAsset(MediaBlobState state) => new()
    {
        FileName = "asset.png",
        MimeType = "image/png",
        StorageKey = "cmsify/media/key",
        StorageProvider = "local",
        BlobState = state
    };
}
