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
        repository.PrepareDeletionAsync(Arg.Any<MediaDeletionClaim>(), now, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns(DeletionPreparationResult.Ready);
        storage.DeleteAsync("failed", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("provider detail"));
        var processor = CreateProcessor(repository, storage, new FixedTimeProvider(now));

        await processor.RunCycleAsync("worker", now);

        await storage.Received(1).DeleteAsync("success", Arg.Any<CancellationToken>());
        await repository.Received(1).CompleteDeletionAsync(success, now, Arg.Any<CancellationToken>());
        await repository.Received(1).RetryDeletionAsync(
            failed, now, now.AddSeconds(3_600), "io", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycle_RechecksOrphanOwnershipImmediatelyBeforeDelete()
    {
        var now = DateTimeOffset.Parse("2026-08-27T16:30:00Z");
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var orphan = Claim("claimed-orphan", 0, "orphan");
        repository.ClaimDeletionIntentsAsync("worker", now, TimeSpan.FromSeconds(300), 100, Arg.Any<CancellationToken>())
            .Returns([orphan]);
        repository.PrepareDeletionAsync(orphan, now, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns(DeletionPreparationResult.Owned);

        await CreateProcessor(repository, storage, new FixedTimeProvider(now)).RunCycleAsync("worker", now);

        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().CompleteDeletionAsync(Arg.Any<MediaDeletionClaim>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycle_UsesFreshTimeForFenceAndDoesNotReportStaleCompletionAsSuccess()
    {
        var cycleStarted = DateTimeOffset.Parse("2026-08-27T16:45:00Z");
        var beforeDelete = cycleStarted.AddMinutes(1);
        var afterDelete = cycleStarted.AddMinutes(2);
        var timeProvider = new SequenceTimeProvider(beforeDelete, afterDelete);
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var claim = Claim("slow-delete", 0);
        repository.ClaimDeletionIntentsAsync("worker", cycleStarted, TimeSpan.FromSeconds(300), 100, Arg.Any<CancellationToken>())
            .Returns([claim]);
        repository.PrepareDeletionAsync(claim, beforeDelete, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns(DeletionPreparationResult.Ready);
        repository.CompleteDeletionAsync(claim, afterDelete, Arg.Any<CancellationToken>()).Returns(false);

        await CreateProcessor(repository, storage, timeProvider).RunCycleAsync("worker", cycleStarted);

        await repository.Received(1).PrepareDeletionAsync(claim, beforeDelete, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>());
        await repository.Received(1).CompleteDeletionAsync(claim, afterDelete, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycle_MarksMissingAndReappearingBlobs()
    {
        var now = DateTimeOffset.Parse("2026-08-27T17:00:00Z");
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var available = new MediaVerificationCandidate(Guid.NewGuid(), "local", "missing", MediaBlobState.Available);
        var missing = new MediaVerificationCandidate(Guid.NewGuid(), "local", "present", MediaBlobState.Missing);
        repository.GetVerificationBatchAsync("local", 100, Arg.Any<CancellationToken>()).Returns([available, missing]);
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

    [Fact]
    public async Task RunCycle_UsesFreshCheckpointTimesAndHonorsLostCompletionFence()
    {
        var cycleStarted = DateTimeOffset.Parse("2026-08-27T19:00:00Z");
        var claimedAt = cycleStarted.AddMinutes(1);
        var completedAt = cycleStarted.AddMinutes(2);
        var secondPrefixClaimedAt = cycleStarted.AddMinutes(3);
        var repository = Substitute.For<IMediaReconciliationRepository>();
        var storage = Substitute.For<IStorageProvider>();
        var checkpoint = new MediaCheckpointClaim(
            Guid.NewGuid(), "local", "cmsify/media/", null, "worker", Guid.NewGuid(), false);
        repository.ClaimCheckpointAsync(
                "local", "cmsify/media/", "worker", claimedAt, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns(checkpoint);
        repository.CompleteCheckpointAsync(
                checkpoint, null, true, completedAt, Arg.Any<CancellationToken>())
            .Returns(false);
        repository.ClaimCheckpointAsync(
                "local", "default/", "worker", secondPrefixClaimedAt, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>())
            .Returns((MediaCheckpointClaim?)null);
        storage.ListAsync(Arg.Any<ListStorageObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StorageObjectPage([], null));

        await CreateProcessor(
            repository,
            storage,
            new SequenceTimeProvider(claimedAt, completedAt, secondPrefixClaimedAt))
            .RunCycleAsync("worker", cycleStarted);

        await repository.Received(1).CompleteCheckpointAsync(
            checkpoint, null, true, completedAt, Arg.Any<CancellationToken>());
    }

    private static MediaReconciliationProcessor CreateProcessor(
        IMediaReconciliationRepository repository,
        IStorageProvider storage,
        TimeProvider? timeProvider = null) => new(
            repository,
            storage,
            Options.Create(new MediaOperationalOptions()),
            "local",
            NullLogger<MediaReconciliationProcessor>.Instance,
            timeProvider);

    private static MediaDeletionClaim Claim(string key, int attempts, string reason = "unknown") => new(
        Guid.NewGuid(), null, "local", key, attempts, "worker", Guid.NewGuid(), false, reason);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow() => values[Math.Min(index++, values.Length - 1)];
    }
}
