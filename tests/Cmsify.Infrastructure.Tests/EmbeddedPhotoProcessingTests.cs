using Cmsify.Core.EmbeddedMedia;
using System.Diagnostics;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoProcessingTests
{
    [Fact]
    public async Task ChangedIntentKeyRacingAcrossAssetsReturnsConflict()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        var firstAsset = await f.Upload(); var secondAsset = await f.Upload(); var key = Guid.NewGuid();
        await using var firstDb = f.Context(); await using var secondDb = f.Context();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int arrived = 0;
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>> CommitGate()
        {
            int calls = 0;
            return async (_, token) =>
            {
                if (++calls == 2)
                {
                    if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
                    await Task.WhenAny(both.Task, Task.Delay(TimeSpan.FromSeconds(1), token));
                }
                return true;
            };
        }
        var first = f.Repository(firstDb).CreateRevisionAsync(new(f.WorkspaceId, firstAsset, key, 1, new(1), f.Settings.PolicyId), f.ActorId, CommitGate(), Ct);
        var second = f.Repository(secondDb).CreateRevisionAsync(new(f.WorkspaceId, secondAsset, key, 1, new(1), f.Settings.PolicyId), f.ActorId, CommitGate(), Ct);
        var results = await Task.WhenAll(first, second);
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => r.IsFailure).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
    }

    [Fact]
    public Task KillAfterClaimRecoversUnderNewFence() => CrashAndRecover("processing-after-claim", false);
    [Fact]
    public Task KillDuringVariantsLeavesNoReadyManifest() => CrashAndRecover("processing-during-variants", false);
    [Fact]
    public Task KillAfterManifestPreservesExactVariants() => CrashAndRecover("processing-after-manifest", true);

    private static async Task CrashAndRecover(string stage, bool ready)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context();
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/Cmsify.Photo.CrashHost/bin/Release/net10.0/Cmsify.Photo.CrashHost.dll"));
        File.Exists(host).ShouldBeTrue(); Directory.CreateDirectory(f.Root);
        var marker = Path.Combine(f.Root, "processing-boundary.marker");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { host, stage, db.Database.GetDbConnection().ConnectionString, f.Root,
            f.WorkspaceId.ToString(), asset.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            f.ActorId.ToString(), marker }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(Ct); var error = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50, Ct);
            File.Exists(marker).ShouldBeTrue(child.HasExited ? "Host did not reach its boundary: " + await error : "Boundary timeout.");
            child.HasExited.ShouldBeFalse(); child.Kill(entireProcessTree: true); await child.WaitForExitAsync(Ct);
            child.ExitCode.ShouldNotBe(0); _ = await output; _ = await error;
            await using var restarted = f.Context();
            (await restarted.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(1);
            (await restarted.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);
            (await restarted.PhotoManifests.CountAsync(Ct)).ShouldBe(ready ? 1 : 0);
            if (ready)
            {
                var exact = await File.ReadAllTextAsync(Path.Combine(f.Root, "manifest-id"), Ct);
                (await restarted.PhotoManifests.SingleAsync(Ct)).ManifestSha256.ShouldBe(exact);
                (await f.Worker(restarted, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
            }
            else
            {
                (await restarted.PhotoVariants.CountAsync(Ct)).ShouldBe(0);
                f.Time.Now += TimeSpan.FromMinutes(2);
                (await f.Worker(restarted, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
                f.Time.Now += TimeSpan.FromSeconds(5);
                (await f.Worker(restarted, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
                (await restarted.PhotoJobs.AsNoTracking().SingleAsync(Ct)).AutomaticAttemptCount.ShouldBe(2);
            }
            (await restarted.PhotoVariants.CountAsync(Ct)).ShouldBe(3);
            foreach (var variant in await restarted.PhotoVariants.AsNoTracking().ToListAsync(Ct))
            {
                await using var stored = await f.Storage.Provider.ReadAsync(variant.StorageKey, Ct);
                stored.ShouldNotBeNull();
                var sha = await System.Security.Cryptography.SHA256.HashDataAsync(stored.Content, Ct);
                Convert.ToHexString(sha).ShouldBe(variant.Sha256);
            }
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(Ct); } }
    }

    [Fact]
    public async Task ReadyStatusDiscoversExactManifestWithoutCmsSql()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context(); var repository = f.Repository(db);
        (await repository.GetAsync(new(f.WorkspaceId, asset, 1), f.ActorId, Permit, Ct)).Value.Manifest.ShouldBeNull();
        await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct);
        var status = (await repository.GetAsync(new(f.WorkspaceId, asset, 1), f.ActorId, Permit, Ct)).Value;
        status.Manifest.ShouldNotBeNull();
        var manifest = (await repository.GetManifestAsync(new(f.WorkspaceId, asset, 1, status.Manifest.ManifestSha256), f.ActorId, Permit, Ct)).Value;
        manifest.Identity.ShouldBe(status.Manifest);
    }

    [Fact]
    public async Task OneSiteAttemptAndStaleCompletionAreFenced()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var firstDb = f.Context(); await using var secondDb = f.Context();
        var gate = new GateProcessor();
        var first = f.Worker(firstDb, gate).ProcessNextAsync(f.ActorId, Ct);
        await gate.Started.Task.WaitAsync(Ct);
        try
        {
            (await f.Worker(secondDb, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
            (await secondDb.PhotoJobs.CountAsync(x => x.LeaseExpiresAt > f.Time.Now, Ct)).ShouldBe(1);
            f.Time.Now += TimeSpan.FromMinutes(1);
            (await f.Worker(secondDb, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
            f.Time.Now += TimeSpan.FromSeconds(5);
            (await f.Worker(secondDb, new CandidateProcessor(marker: 7)).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        }
        finally { gate.Resume.TrySetResult(); }
        (await first).ShouldBeTrue();
        await using var check = f.Context();
        (await check.PhotoManifests.CountAsync(Ct)).ShouldBe(1);
        (await check.PhotoVariants.CountAsync(Ct)).ShouldBe(3);
        (await check.PhotoJobs.SingleAsync(Ct)).AutomaticAttemptCount.ShouldBe(2);
        (await check.PhotoRevisions.SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Ready);
        (await check.PhotoRevisions.SingleAsync(Ct)).AssetId.ShouldBe(asset);
    }

    [Fact]
    public async Task TransientBudgetIsThreeWithFiveThenThirtySeconds()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await f.Upload();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var db = f.Context();
            (await f.Worker(db, new FailingProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
            var job = await db.PhotoJobs.AsNoTracking().SingleAsync(Ct);
            job.AutomaticAttemptCount.ShouldBe(attempt);
            if (attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(attempt == 1 ? 5 : 30);
                (job.NextAttemptAt - f.Time.Now).ShouldBe(delay);
                job.State.ShouldBe(EmbeddedPhotoState.Queued);
                f.Time.Now += delay;
            }
            else job.State.ShouldBe(EmbeddedPhotoState.Failed);
        }
        await using var final = f.Context();
        (await f.Worker(final, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task RejectedCannotRetryAndFailedRetryIsKeyedGeneration()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        for (var i = 0; i < 3; i++)
        {
            await using var db = f.Context();
            await f.Worker(db, new FailingProcessor()).ProcessNextAsync(f.ActorId, Ct);
            f.Time.Now += TimeSpan.FromSeconds(i == 0 ? 5 : 30);
        }
        await using var retryDb = f.Context();
        var originalJob = await retryDb.PhotoJobs.AsNoTracking().SingleAsync(Ct);
        var request = new RetryEmbeddedPhotoProcessingRequest(f.WorkspaceId, asset, 1, Guid.NewGuid(), 0);
        var retry = await f.Repository(retryDb).RetryAsync(request, f.ActorId, Permit, Ct);
        retry.IsSuccess.ShouldBeTrue(); retry.Value.RetryGeneration.ShouldBe(1);
        var replay = await f.Repository(retryDb).RetryAsync(request, f.ActorId, Permit, Ct);
        replay.Value.ShouldBe(retry.Value);
        (await f.Repository(retryDb).RetryAsync(request with { ExpectedRetryGeneration = 1 }, f.ActorId, Permit, Ct))
            .Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        (await retryDb.PhotoJobs.AsNoTracking().SingleAsync(Ct)).Id.ShouldBe(originalJob.Id);
        await f.Worker(retryDb, new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId, Ct);
        var rejected = await f.Repository(retryDb).RetryAsync(request with { OperationKey = Guid.NewGuid(), ExpectedRetryGeneration = 1 }, f.ActorId, Permit, Ct);
        rejected.IsFailure.ShouldBeTrue();
        (await retryDb.PhotoRevisions.AsNoTracking().SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Rejected);
    }

    [Fact]
    public async Task RotateCreatesRevisionAndLeavesOldManifestUntouched()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context();
        await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct);
        var original = await db.PhotoManifests.AsNoTracking().SingleAsync(Ct);
        var oldRead = new GetEmbeddedPhotoManifestRequest(f.WorkspaceId, asset, 1, original.ManifestSha256);
        var before = (await f.Repository(db).GetManifestAsync(oldRead, f.ActorId, Permit, Ct)).Value;
        var request = new CreateEmbeddedPhotoRevisionRequest(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(1), f.Settings.PolicyId);
        var revision = await f.Repository(db).CreateRevisionAsync(request, f.ActorId, Permit, Ct);
        revision.Value.Revision.ShouldBe(2);
        (await f.Repository(db).CreateRevisionAsync(request, f.ActorId, Permit, Ct)).Value.ShouldBe(revision.Value);
        (await f.Repository(db).CreateRevisionAsync(request with { Instructions = new(2) }, f.ActorId, Permit, Ct))
            .Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct);
        var after = (await f.Repository(db).GetManifestAsync(oldRead, f.ActorId, Permit, Ct)).Value;
        after.Identity.ShouldBe(before.Identity); after.Variants.ShouldBe(before.Variants);
        (await db.PhotoVariants.CountAsync(Ct)).ShouldBe(6);
        var rotated = await db.PhotoVariants.AsNoTracking().SingleAsync(x => x.Revision == 2 && x.VariantKey == "320", Ct);
        rotated.Width.ShouldBe(240); rotated.Height.ShouldBe(320);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("oversized")]
    [InlineData("checksum")]
    [InlineData("dimensions")]
    [InlineData("content")]
    [InlineData("container")]
    [InlineData("metadata")]
    [InlineData("path")]
    [InlineData("bitDepth")]
    [InlineData("color")]
    [InlineData("alpha")]
    public async Task UntrustedCandidateCannotBecomeReady(string defect)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await f.Upload();
        await using var db = f.Context();
        var processor = new CandidateProcessor(defect);
        (await f.Worker(db, processor).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        (await db.PhotoManifests.CountAsync(Ct)).ShouldBe(0);
        (await db.PhotoVariants.CountAsync(Ct)).ShouldBe(0);
        (await db.PhotoRevisions.AsNoTracking().SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Rejected);
        processor.CleanupCount.ShouldBe(1);
    }

    [Fact]
    public async Task AlphaAndSmallSourceHaveEqualNonEnlargedBounds()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await f.Upload();
        await using var db = f.Context();
        (await f.Worker(db, new CandidateProcessor(width: 11, height: 7, alpha: true)).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        var variants = await db.PhotoVariants.AsNoTracking().ToListAsync(Ct);
        variants.Count.ShouldBe(3); variants.ShouldAllBe(x => x.Width == 11 && x.Height == 7);
    }

    [Fact]
    public async Task MissingWorkerAuthorityOrCapabilityOpensNoProcessor()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await f.Upload();
        await using var db = f.Context(); var gate = new GateProcessor();
        f.Authorization.Allowed = false;
        (await f.Worker(db, gate).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
        gate.Started.Task.IsCompleted.ShouldBeFalse();
        f.Authorization.Allowed = true; f.Capability.Available = false;
        (await f.Worker(db, gate).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
        gate.Started.Task.IsCompleted.ShouldBeFalse();
        (await db.PhotoJobs.AsNoTracking().SingleAsync(Ct)).AutomaticAttemptCount.ShouldBe(0);
    }
}
