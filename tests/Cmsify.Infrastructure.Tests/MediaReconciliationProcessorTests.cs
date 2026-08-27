using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class MediaReconciliationProcessorTests
{
    [Fact]
    public async Task RunCycle_CompletesDeletesAndRetriesFailuresWithCappedBackoff()
    {
        var now = DateTimeOffset.Parse("2026-08-27T16:00:00Z");
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var success = Claim("success", 0);
        var failed = Claim("failed", 7);
        repository.ClaimDeletionIntentsAsync("worker", now, TimeSpan.FromSeconds(300), 100, Arg.Any<CancellationToken>())
            .Returns([success, failed]);
        storage.DeleteAsync("failed", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("provider detail"));
        var processor = CreateProcessor(repository, storage);

        await processor.RunCycleAsync("worker", now);

        await storage.Received(1).DeleteAsync("success", Arg.Any<CancellationToken>());
        await repository.Received(1).CompleteDeletionAsync(success, now, Arg.Any<CancellationToken>());
        await repository.Received(1).RetryDeletionAsync(
            failed, now, now.AddSeconds(3_600), "IOException", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycle_MarksMissingAndReappearingBlobs()
    {
        var now = DateTimeOffset.Parse("2026-08-27T17:00:00Z");
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var available = new MediaVerificationCandidate(Guid.NewGuid(), "local", "missing", MediaBlobState.Available);
        var missing = new MediaVerificationCandidate(Guid.NewGuid(), "local", "present", MediaBlobState.Missing);
        repository.GetVerificationBatchAsync(100, Arg.Any<CancellationToken>()).Returns([available, missing]);
        storage.GetMetadataAsync("missing", Arg.Any<CancellationToken>()).Returns((StorageObjectMetadata?)null);
        storage.GetMetadataAsync("present", Arg.Any<CancellationToken>()).Returns(
            new StorageObjectMetadata("present", 1, null, now));

        await CreateProcessor(repository, storage).RunCycleAsync("worker", now);

        await repository.Received(1).RecordBlobMissingAsync(available.Id, now, Arg.Any<CancellationToken>());
        await repository.Received(1).RecordBlobPresentAsync(missing.Id, now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycle_QueuesOnlyOldUntrackedObjectsAndPersistsContinuation()
    {
        var now = DateTimeOffset.Parse("2026-08-27T18:00:00Z");
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var checkpoint = new MediaCheckpointClaim(Guid.NewGuid(), "local", "cmsify/media/", null, "worker", Guid.NewGuid(), false);
        repository.ClaimCheckpointAsync("local", "cmsify/media/", "worker", now, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns(checkpoint);
        repository.ClaimCheckpointAsync("local", "default/", "worker", now, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns((MediaCheckpointClaim?)null);
        storage.ListAsync(Arg.Any<ListStorageObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(
            new StorageObjectPage(
            [
                new StorageObjectMetadata("cmsify/media/old", 1, null, now.AddHours(-24)),
                new StorageObjectMetadata("cmsify/media/young", 1, null, now.AddHours(-24).AddTicks(1)),
                new StorageObjectMetadata("cmsify/media/tracked", 1, null, now.AddDays(-2))
            ], "cmsify/media/tracked"));
        repository.StorageKeyExistsAsync("local", "cmsify/media/old", Arg.Any<CancellationToken>()).Returns(false);
        repository.StorageKeyExistsAsync("local", "cmsify/media/young", Arg.Any<CancellationToken>()).Returns(false);
        repository.StorageKeyExistsAsync("local", "cmsify/media/tracked", Arg.Any<CancellationToken>()).Returns(true);

        await CreateProcessor(repository, storage).RunCycleAsync("worker", now);

        await repository.Received(1).EnqueueOrphanDeletionAsync("local", "cmsify/media/old", now, Arg.Any<CancellationToken>());
        await repository.DidNotReceive().EnqueueOrphanDeletionAsync("local", "cmsify/media/young", now, Arg.Any<CancellationToken>());
        await repository.Received(1).CompleteCheckpointAsync(
            checkpoint, "cmsify/media/tracked", false, now, Arg.Any<CancellationToken>());
    }

    private static MediaReconciliationProcessor CreateProcessor(
        IMediaReconciliationRepository repository,
        IStorageProvider storage) => new(
            repository,
            storage,
            Options.Create(new MediaOperationalOptions()),
            "local",
            NullLogger<MediaReconciliationProcessor>.Instance);

    private static MediaDeletionClaim Claim(string key, int attempts) => new(
        Guid.NewGuid(), null, "local", key, attempts, "worker", Guid.NewGuid(), false);
}
