using System.Data.Common;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

[Collection(OperationalMetricsTestGroup.Name)]
public sealed class SqliteMediaReconciliationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T18:15:12.1234567+05:30");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static Guid Id(int n) => Guid.Parse($"abcdef12-3456-789a-bcde-{n:D12}");

    [Theory]
    [InlineData("prepare")]
    [InlineData("retry")]
    [InlineData("complete")]
    [InlineData("checkpoint")]
    public async Task FencedQueries_AcceptSeededNontrivialGuidAndLiveLease(string operation)
    {
        await using var database = await Database.CreateAsync();
        await using (var setup = database.Context())
        {
            var intent = Intent(1); intent.LeaseOwner = "a"; intent.LeaseToken = Id(90); intent.LeaseExpiresAt = Now.Add(Lease);
            setup.Add(intent);
            setup.Add(new MediaReconciliationCheckpoint { Id = Id(2), Provider = "local", Prefix = "cmsify/media/", LeaseOwner = "a", LeaseToken = Id(90), LeaseExpiresAt = Now.Add(Lease) });
            await setup.SaveChangesAsync(Ct);
        }
        await using var context = database.Context();
        var repo = Repository(context);
        var claim = new MediaDeletionClaim(Id(1), null, "local", "cmsify/media/1", 0, "a", Id(90), false, "orphan");
        switch (operation)
        {
            case "prepare": (await repo.PrepareDeletionAsync(claim, Now, Lease, Ct)).ShouldBe(DeletionPreparationResult.Ready); break;
            case "retry": (await repo.RetryDeletionAsync(claim, Now, Now.AddMinutes(1), "io", Ct)).ShouldBeTrue(); break;
            case "complete": (await repo.CompleteDeletionAsync(claim, Now, Ct)).ShouldBeTrue(); break;
            default: (await repo.CompleteCheckpointAsync(new(Id(2), "local", "cmsify/media/", null, "a", Id(90), false), "page", false, Now, Ct)).ShouldBeTrue(); break;
        }
    }

    [Fact]
    public async Task ProcessorCycle_RetainsNewlyOwnedOrphanAndDeletesOnlyUnownedSyntheticBlob()
    {
        await using var database = await Database.CreateAsync();
        await using (var setup = database.Context())
        {
            var owned = Intent(1); owned.StorageKey = "cmsify/media/owned";
            var orphan = Intent(2); orphan.StorageKey = "cmsify/media/unowned";
            setup.AddRange(owned, orphan); await setup.SaveChangesAsync(Ct);
        }
        // Ownership is acquired after orphan discovery and before processor preparation.
        await SeedAssetAsync(database, "owned", MediaBlobState.Available, Now);
        var blobs = new HashSet<string> { "cmsify/media/owned", "cmsify/media/unowned" };
        var storage = Substitute.For<IStorageProvider>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => { blobs.Remove(call.ArgAt<string>(0)); return Task.CompletedTask; });
        storage.GetMetadataAsync("cmsify/media/owned", Arg.Any<CancellationToken>()).Returns(new StorageObjectMetadata("cmsify/media/owned", 1, null, Now));
        storage.ListAsync(Arg.Any<ListStorageObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(new StorageObjectPage([], null));
        await using (var context = database.Context())
            await new MediaReconciliationProcessor(Repository(context), storage, Options.Create(new MediaOperationalOptions { ManagedPrefixes = ["cmsify/media/"] }), "local", NullLogger<MediaReconciliationProcessor>.Instance).RunCycleAsync("a", Now, Ct);
        blobs.ShouldBe(new[] { "cmsify/media/owned" });
        await using var verify = database.Context();
        (await verify.MediaDeletionIntents.ToListAsync(Ct)).ShouldAllBe(i => i.CompletedAt == Now);
        (await verify.MediaReconciliationCheckpoints.SingleAsync(Ct)).LastScanCompletedAt.ShouldBe(Now);
        (await verify.MediaAssets.SingleAsync(Ct)).BlobState.ShouldBe(MediaBlobState.Available);
    }

    [Fact]
    public async Task ProcessorCycle_PersistsRetryBackoffAndResumesListedOrphans()
    {
        await using var database = await Database.CreateAsync();
        await using (var setup = database.Context())
        {
            var intent = Intent(1); intent.AttemptCount = 7;
            setup.Add(intent); await setup.SaveChangesAsync(Ct);
        }
        var requestedCursors = new List<string?>();
        var storage = Substitute.For<IStorageProvider>();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("synthetic"));
        storage.ListAsync(Arg.Any<ListStorageObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.ArgAt<ListStorageObjectsRequest>(0);
            requestedCursors.Add(request.AfterKey);
            return request.AfterKey is null
                ? new StorageObjectPage([new StorageObjectMetadata("cmsify/media/discovered", 1, null, Now.AddDays(-2))], "cmsify/media/discovered")
                : new StorageObjectPage([], null);
        });
        for (var cycle = 0; cycle < 2; cycle++)
        {
            await using var context = database.Context();
            await new MediaReconciliationProcessor(Repository(context), storage, Options.Create(new MediaOperationalOptions { ManagedPrefixes = ["cmsify/media/"] }), "local", NullLogger<MediaReconciliationProcessor>.Instance).RunCycleAsync("a", Now, Ct);
        }
        requestedCursors.ShouldBe(new string?[] { null, "cmsify/media/discovered" });
        await using var verify = database.Context();
        var failed = await verify.MediaDeletionIntents.SingleAsync(i => i.Id == Id(1), Ct);
        failed.AttemptCount.ShouldBe(8); failed.NextAttemptAt.ShouldBe(Now.AddSeconds(3600)); failed.LastError.ShouldBe("io");
        var discovered = await verify.MediaDeletionIntents.SingleAsync(i => i.StorageKey == "cmsify/media/discovered", Ct);
        discovered.AttemptCount.ShouldBe(1); discovered.NextAttemptAt.ShouldBe(Now.AddSeconds(30));
        (await verify.MediaReconciliationCheckpoints.SingleAsync(Ct)).AfterKey.ShouldBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ClaimBatch_LimitsMaterializedWorkAndRetainsRemainder(int limit)
    {
        await using var database = await Database.CreateAsync();
        await using (var setup = database.Context())
        {
            setup.AddRange(Enumerable.Range(1, 1002).Select(Intent)); await setup.SaveChangesAsync(Ct);
        }
        await using var context = database.Context();
        (await Repository(context).ClaimDeletionIntentsAsync("a", Now, Lease, limit, Ct)).Count.ShouldBe(limit);
        context.ChangeTracker.Entries<MediaDeletionIntent>().Count().ShouldBe(limit);
        await using var verify = database.Context();
        (await verify.MediaDeletionIntents.CountAsync(i => i.LeaseToken == null, Ct)).ShouldBe(1002 - limit);
    }

    [Fact]
    public async Task ValidationAndCanceledOperationsLeaveWorkUnchanged()
    {
        await using var database = await Database.CreateAsync();
        await using var context = database.Context();
        context.Add(Intent(1)); await context.SaveChangesAsync(Ct);
        var repo = Repository(context);
        foreach (var limit in new[] { 0, 1001 })
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.ClaimDeletionIntentsAsync("a", Now, Lease, limit, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.FailStaleUploadsAsync(Now, Now, limit, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.GetVerificationBatchAsync("local", limit, Ct));
        }
        foreach (var worker in new[] { " ", new string('a', 201) })
        {
            await Should.ThrowAsync<ArgumentException>(() => repo.ClaimDeletionIntentsAsync(worker, Now, Lease, 1, Ct));
            await Should.ThrowAsync<ArgumentException>(() => repo.ClaimCheckpointAsync("local", "cmsify/media/", worker, Now, Lease, Ct));
        }
        foreach (var duration in new[] { TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1), TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)) })
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.ClaimDeletionIntentsAsync("a", Now, duration, 1, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repo.ClaimCheckpointAsync("local", "cmsify/media/", "a", Now, duration, Ct));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => repo.ClaimDeletionIntentsAsync("a", Now, Lease, 1, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => repo.FailStaleUploadsAsync(Now, Now, 1, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => repo.ClaimCheckpointAsync("local", "cmsify/media/", "a", Now, Lease, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => repo.EnqueueOrphanDeletionAsync("local", "cancel", Now, canceled.Token));
        await using var read = database.Context();
        (await read.MediaDeletionIntents.SingleAsync(Ct)).LeaseToken.ShouldBeNull();
        (await read.MediaReconciliationCheckpoints.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task VerificationStateChanges_FenceStaleSavesAndRespectProviderBeforeLimit()
    {
        await using var database = await Database.CreateAsync();
        var owned = await SeedAssetAsync(database, "local", MediaBlobState.Available, Now);
        var other = await SeedAssetAsync(database, "s3", MediaBlobState.Available, Now.AddDays(-1));
        await using (var setup = database.Context())
        {
            var asset = await setup.MediaAssets.SingleAsync(a => a.Id == other.Id, Ct); asset.StorageProvider = "s3";
            await setup.SaveChangesAsync(Ct);
        }
        await using var stale = database.Context();
        var staleAsset = await stale.MediaAssets.SingleAsync(a => a.Id == owned.Id, Ct);
        await using (var context = database.Context())
        {
            (await Repository(context).GetVerificationBatchAsync("local", 1, Ct)).Single().Id.ShouldBe(owned.Id);
            await Repository(context).RecordBlobMissingAsync(owned.Id, Now, Ct);
        }
        staleAsset.FileName = "stale";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
        await using (var context = database.Context())
        {
            var missing = await context.MediaAssets.SingleAsync(a => a.Id == owned.Id, Ct);
            missing.BlobState.ShouldBe(MediaBlobState.Missing); missing.MissingDetectedAt.ShouldBe(Now);
            context.Entry(missing).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
            await Repository(context).RecordBlobPresentAsync(owned.Id, Now.AddMinutes(1), Ct);
        }
        await using var verify = database.Context();
        var present = await verify.MediaAssets.SingleAsync(a => a.Id == owned.Id, Ct);
        present.BlobState.ShouldBe(MediaBlobState.Available); present.BlobVerifiedAt.ShouldBe(Now.AddMinutes(1)); present.MissingDetectedAt.ShouldBeNull();
        verify.Entry(present).Property<uint>("xmin").CurrentValue.ShouldBe(3u);
    }

    [Fact]
    public async Task DeletionClaims_CompeteForBoundedDisjointNonemptyBatchesAndExcludeIneligibleWork()
    {
        await using var database = await Database.CreateAsync();
        await using (var setup = database.Context())
        {
            setup.AddRange(Enumerable.Range(1, 4).Select(n => Intent(n)));
            var future = Intent(5); future.NotBefore = Now.AddTicks(1);
            var retry = Intent(6); retry.NextAttemptAt = Now.AddTicks(1);
            var completed = Intent(7); completed.CompletedAt = Now;
            var live = Intent(8); live.LeaseExpiresAt = Now.AddTicks(1);
            var expired = Intent(9); expired.LeaseExpiresAt = Now;
            setup.AddRange(future, retry, completed, live, expired);
            await setup.SaveChangesAsync(Ct);
        }
        await using var firstContext = database.Context();
        await using var secondContext = database.Context();
        var batches = await Task.WhenAll(
            Task.Run(() => Repository(firstContext).ClaimDeletionIntentsAsync("a", Now, Lease, 2, Ct), Ct),
            Task.Run(() => Repository(secondContext).ClaimDeletionIntentsAsync("b", Now, Lease, 2, Ct), Ct));
        batches.ShouldAllBe(batch => batch.Count == 2);
        batches.SelectMany(batch => batch).Select(claim => claim.Id).Distinct().Count().ShouldBe(4);
        await using var rest = database.Context();
        var last = (await Repository(rest).ClaimDeletionIntentsAsync("c", Now, Lease, 2, Ct)).Single();
        last.Id.ShouldBe(Id(9));
        last.WasReclaimed.ShouldBeTrue();
        batches.SelectMany(batch => batch).Select(claim => claim.Id).Order().ShouldBe(Enumerable.Range(1, 4).Select(Id).Order());
    }

    [Fact]
    public async Task FencedDeletion_RetryAndCompleteRoundtripAssetStateAndRejectLostClaims()
    {
        await using var database = await Database.CreateAsync();
        var asset = await SeedAssetAsync(database, "deleted", MediaBlobState.DeletePending, Now);
        await using (var setup = database.Context())
        {
            var intent = Intent(1); intent.MediaAssetId = asset.Id;
            setup.Add(intent); await setup.SaveChangesAsync(Ct);
        }
        MediaDeletionClaim first;
        await using (var context = database.Context())
            first = (await Repository(context).ClaimDeletionIntentsAsync("a", Now, Lease, 1, Ct)).Single();
        foreach (var wrong in new[] { first with { LeaseOwner = "wrong" }, first with { LeaseToken = Id(999) } })
            await AssertLostAsync(database, wrong, Now);
        await AssertLostAsync(database, first, Now.Add(Lease));
        MediaDeletionClaim reclaimed;
        await using (var context = database.Context())
        {
            reclaimed = (await Repository(context).ClaimDeletionIntentsAsync("b", Now.Add(Lease), Lease, 1, Ct)).Single();
            reclaimed.WasReclaimed.ShouldBeTrue();
        }
        await AssertLostAsync(database, first, Now.Add(Lease));
        await using (var context = database.Context())
        {
            var repo = Repository(context);
            (await repo.PrepareDeletionAsync(reclaimed, Now.Add(Lease), Lease, Ct)).ShouldBe(DeletionPreparationResult.Ready);
            (await repo.RetryDeletionAsync(reclaimed, Now.Add(Lease), Now.AddMinutes(6), new string('x', 3000), Ct)).ShouldBeTrue();
        }
        await using (var read = database.Context())
        {
            var saved = await read.MediaDeletionIntents.SingleAsync(Ct);
            saved.AttemptCount.ShouldBe(1); saved.NextAttemptAt.ShouldBe(Now.AddMinutes(6));
            saved.LastError!.Length.ShouldBe(2000); saved.LeaseToken.ShouldBeNull();
            (await read.MediaAssets.SingleAsync(Ct)).BlobState.ShouldBe(MediaBlobState.DeletePending);
            (await Repository(read).ClaimDeletionIntentsAsync("c", Now.AddMinutes(6).AddTicks(-1), Lease, 1, Ct)).ShouldBeEmpty();
        }
        MediaDeletionClaim final;
        await using (var context = database.Context())
        {
            final = (await Repository(context).ClaimDeletionIntentsAsync("c", Now.AddMinutes(6), Lease, 1, Ct)).Single();
            final.AttemptCount.ShouldBe(1);
            (await Repository(context).CompleteDeletionAsync(final, Now.AddMinutes(6), Ct)).ShouldBeTrue();
        }
        await AssertLostAsync(database, final, Now.AddMinutes(6));
        await using var verify = database.Context();
        var deleted = await verify.MediaAssets.SingleAsync(Ct);
        deleted.BlobState.ShouldBe(MediaBlobState.Deleted);
        verify.Entry(deleted).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
        (await verify.MediaDeletionIntents.SingleAsync(Ct)).CompletedAt.ShouldBe(Now.AddMinutes(6));
    }

    [Fact]
    public async Task StaleUploads_TransitionAndEnqueueAtomicallyWithInclusiveBoundAndRevision()
    {
        await using var database = await Database.CreateAsync();
        var cutoff = Now.AddMinutes(-30);
        var old = await SeedAssetAsync(database, "old", MediaBlobState.PendingUpload, cutoff);
        await SeedAssetAsync(database, "new", MediaBlobState.PendingUpload, cutoff.AddTicks(1));
        await SeedAssetAsync(database, "hidden", MediaBlobState.PendingUpload, cutoff, deleted: true);
        await using (var context = database.Context())
            (await Repository(context).FailStaleUploadsAsync(cutoff, Now, 1, Ct)).ShouldBe(1);
        await using var verify = database.Context();
        var assets = await verify.MediaAssets.IgnoreQueryFilters().ToDictionaryAsync(a => a.FileName, Ct);
        assets["old"].BlobState.ShouldBe(MediaBlobState.UploadFailed);
        verify.Entry(assets["old"]).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
        assets["new"].BlobState.ShouldBe(MediaBlobState.PendingUpload);
        assets["hidden"].BlobState.ShouldBe(MediaBlobState.PendingUpload);
        var intent = await verify.MediaDeletionIntents.SingleAsync(Ct);
        intent.MediaAssetId.ShouldBe(old.Id); intent.Reason.ShouldBe("abandoned_upload");
        intent.NotBefore.ShouldBe(Now); intent.NextAttemptAt.ShouldBe(Now);
    }

    [Fact]
    public async Task StaleUploads_DatabaseFailureRollsBackAssetAndIntent()
    {
        await using var database = await Database.CreateAsync();
        await SeedAssetAsync(database, "old", MediaBlobState.PendingUpload, Now.AddHours(-1));
        await using (var context = database.Context())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_intent BEFORE INSERT ON media_deletion_intents BEGIN SELECT RAISE(ABORT, 'synthetic insert failure'); END", Ct);
            var exception = await Should.ThrowAsync<DbUpdateException>(() => Repository(context).FailStaleUploadsAsync(Now, Now, 2, Ct));
            exception.InnerException!.Message.ShouldContain("synthetic insert failure");
        }
        await using var verify = database.Context();
        var asset = await verify.MediaAssets.SingleAsync(Ct);
        asset.BlobState.ShouldBe(MediaBlobState.PendingUpload);
        verify.Entry(asset).Property<uint>("xmin").CurrentValue.ShouldBe(1u);
        (await verify.MediaDeletionIntents.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task OrphanInsert_DeduplicatesPendingAndAllowsCompletedHistoryWithUtcTicksAndGuidRoundtrip()
    {
        await using var database = await Database.CreateAsync();
        await using (var context = database.Context())
        {
            await Repository(context).EnqueueOrphanDeletionAsync("local", "cmsify/media/orphan", Now, Ct);
            await Repository(context).EnqueueOrphanDeletionAsync("local", "cmsify/media/orphan", Now.AddHours(1), Ct);
        }
        MediaDeletionClaim claim;
        await using (var context = database.Context())
        {
            var saved = await context.MediaDeletionIntents.SingleAsync(Ct);
            saved.Id.ShouldNotBe(Guid.Empty); saved.NotBefore.ShouldBe(Now); saved.NextAttemptAt.ShouldBe(Now); saved.CreatedAt.ShouldBe(Now);
            claim = (await Repository(context).ClaimDeletionIntentsAsync("a", Now, Lease, 1, Ct)).Single();
            claim.Id.ShouldBe(saved.Id);
            (await Repository(context).CompleteDeletionAsync(claim, Now, Ct)).ShouldBeTrue();
        }
        await using (var context = database.Context())
            await Repository(context).EnqueueOrphanDeletionAsync("local", "cmsify/media/orphan", Now, Ct);
        await using var verify = database.Context();
        (await verify.MediaDeletionIntents.CountAsync(Ct)).ShouldBe(2);
        (await verify.MediaDeletionIntents.CountAsync(i => i.CompletedAt == null, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Checkpoint_CompetitionReclaimFencingResumeAndPrefixResetRoundtrip()
    {
        await using var database = await Database.CreateAsync();
        await using var a = database.Context();
        await using var b = database.Context();
        var claims = await Task.WhenAll(
            Task.Run(() => Repository(a).ClaimCheckpointAsync("local", "cmsify/media/", "a", Now, Lease, Ct), Ct),
            Task.Run(() => Repository(b).ClaimCheckpointAsync("local", "cmsify/media/", "b", Now, Lease, Ct), Ct));
        var first = claims.Single(c => c != null)!;
        await using (var context = database.Context())
        {
            (await context.MediaReconciliationCheckpoints.CountAsync(Ct)).ShouldBe(1);
            var checkpoint = await context.MediaReconciliationCheckpoints.SingleAsync(Ct);
            checkpoint.Id.ShouldBe(first.Id); checkpoint.CreatedAt.ShouldBe(Now); checkpoint.LastScanStartedAt.ShouldBe(Now);
            (await Repository(context).CompleteCheckpointAsync(first with { LeaseOwner = "wrong" }, "bad", false, Now, Ct)).ShouldBeFalse();
            (await Repository(context).CompleteCheckpointAsync(first with { LeaseToken = Id(99) }, "bad", false, Now, Ct)).ShouldBeFalse();
            (await Repository(context).CompleteCheckpointAsync(first, "bad", false, Now.Add(Lease), Ct)).ShouldBeFalse();
        }
        await using (var context = database.Context())
        {
            var repo = Repository(context);
            var reclaimed = (await repo.ClaimCheckpointAsync("local", "cmsify/media/", "c", Now.Add(Lease), Lease, Ct))!;
            reclaimed.WasReclaimed.ShouldBeTrue();
            (await repo.CompleteCheckpointAsync(first, "bad", false, Now.Add(Lease), Ct)).ShouldBeFalse();
            (await repo.CompleteCheckpointAsync(reclaimed, "cmsify/media/page-one", false, Now.Add(Lease), Ct)).ShouldBeTrue();
            (await repo.CompleteCheckpointAsync(reclaimed, "bad", false, Now.Add(Lease), Ct)).ShouldBeFalse();
        }
        await using (var context = database.Context())
        {
            var repo = Repository(context);
            var resumed = (await repo.ClaimCheckpointAsync("local", "cmsify/media/", "d", Now.AddMinutes(6), Lease, Ct))!;
            resumed.AfterKey.ShouldBe("cmsify/media/page-one");
            (await repo.CompleteCheckpointAsync(resumed, "ignored", true, Now.AddMinutes(6), Ct)).ShouldBeTrue();
        }
        await using var verify = database.Context();
        var final = await verify.MediaReconciliationCheckpoints.SingleAsync(Ct);
        final.AfterKey.ShouldBeNull(); final.LastScanCompletedAt.ShouldBe(Now.AddMinutes(6)); final.LeaseToken.ShouldBeNull();
    }

    [Theory]
    [InlineData("media_deletion_intents")]
    [InlineData("media_assets")]
    [InlineData("media_reconciliation_checkpoints")]
    public async Task HeldSelection_IndependentWriterWaitsBeforeSelecting(string table)
    {
        await using var database = await Database.CreateAsync();
        if (table == "media_deletion_intents")
        {
            await using var setup = database.Context(); setup.AddRange(Intent(1), Intent(2)); await setup.SaveChangesAsync(Ct);
        }
        if (table == "media_assets")
        {
            await SeedAssetAsync(database, "one", MediaBlobState.PendingUpload, Now.AddHours(-1));
            await SeedAssetAsync(database, "two", MediaBlobState.PendingUpload, Now.AddHours(-1));
        }
        var pause = new PauseSelection(table);
        var attempted = new ObserveTransactionStart();
        await using var firstContext = database.Context(pause);
        await using var secondContext = database.Context(attempted);
        firstContext.Database.GetDbConnection().ShouldNotBeSameAs(secondContext.Database.GetDbConnection());
        async Task<int> Act(CmsifyDbContext context, string worker) => table switch
        {
            "media_deletion_intents" => (await Repository(context).ClaimDeletionIntentsAsync(worker, Now, Lease, 1, Ct)).Count,
            "media_assets" => await Repository(context).FailStaleUploadsAsync(Now, Now, 1, Ct),
            _ => await Repository(context).ClaimCheckpointAsync("local", "cmsify/media/", worker, Now, Lease, Ct) is null ? 0 : 1
        };
        var first = Task.Run(() => Act(firstContext, "a"), Ct);
        Task<int>? second = null;
        try
        {
            await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            pause.Transaction.ShouldNotBeNull();
            second = Task.Run(() => Act(secondContext, "b"), Ct);
            await attempted.Starting.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            await Task.Delay(250, Ct);
            second.IsCompleted.ShouldBeFalse();
            attempted.Started.ShouldBeFalse();
        }
        finally
        {
            pause.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        (await first).ShouldBe(1);
        (await second!).ShouldBe(table == "media_reconciliation_checkpoints" ? 0 : 1);
        await using var read = database.Context();
        if (table == "media_deletion_intents")
            (await read.MediaDeletionIntents.Select(i => i.LeaseOwner).ToListAsync(Ct)).Order().ShouldBe(new[] { "a", "b" });
        if (table == "media_assets") (await read.MediaDeletionIntents.CountAsync(Ct)).ShouldBe(2);
    }

    private static MediaReconciliationRepository Repository(CmsifyDbContext context) => new(context);
    private static MediaDeletionIntent Intent(int n) => new()
    {
        Id = Id(n), Provider = "local", StorageKey = $"cmsify/media/{n}", Reason = "orphan", NotBefore = Now, NextAttemptAt = Now, CreatedAt = Now
    };
    private static async Task AssertLostAsync(Database database, MediaDeletionClaim claim, DateTimeOffset at)
    {
        await using var context = database.Context(); var repo = Repository(context);
        (await repo.PrepareDeletionAsync(claim, at, Lease, Ct)).ShouldBe(DeletionPreparationResult.ClaimLost);
        (await repo.RetryDeletionAsync(claim, at, at.AddMinutes(1), "lost", Ct)).ShouldBeFalse();
        (await repo.CompleteDeletionAsync(claim, at, Ct)).ShouldBeFalse();
    }
    private static async Task<MediaAsset> SeedAssetAsync(Database database, string name, MediaBlobState state, DateTimeOffset changedAt, bool deleted = false)
    {
        await using var context = database.Context();
        var workspace = new Workspace { Name = name, Slug = name };
        var asset = new MediaAsset { WorkspaceId = workspace.Id, FileName = name, MimeType = "image/png", SizeBytes = 1,
            StorageProvider = "local", StorageKey = $"cmsify/media/{name}", BlobState = state, BlobStateChangedAt = changedAt, IsDeleted = deleted };
        context.AddRange(workspace, asset); await context.SaveChangesAsync(Ct); return asset;
    }
    private sealed class Database(string path) : IAsyncDisposable
    {
        public static async Task<Database> CreateAsync()
        {
            var database = new Database(Path.Combine(Path.GetTempPath(), $"cmsify-media-{Guid.NewGuid():N}.db"));
            await using var context = database.Context(); await context.Database.EnsureCreatedAsync(Ct); return database;
        }
        public CmsifyDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSyntaxCircusSnakeCaseNamingConvention().AddInterceptors(interceptors).Options);
        public async ValueTask DisposeAsync() { await using var context = Context(); await context.Database.EnsureDeletedAsync(CancellationToken.None); }
    }
    private sealed class PauseSelection(string table) : DbCommandInterceptor
    {
        private int paused;
        public TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbTransaction? Transaction { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Transaction = command.Transaction; Selected.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
    private sealed class ObserveTransactionStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; private set; }
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        { Starting.TrySetResult(); return ValueTask.FromResult(result); }
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData data, DbTransaction result, CancellationToken cancellationToken = default)
        { Started = true; return ValueTask.FromResult(result); }
    }
}
