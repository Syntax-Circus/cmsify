using System.Diagnostics;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Media;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using SyntaxCircus.Storage;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedMediaReferenceTests
{
    private sealed class Guard : IEmbeddedMediaRetentionGuard
    {
        public EmbeddedMediaRetentionDecision Decision { get; set; } = EmbeddedMediaRetentionDecision.Unknown;
        public bool Throw { get; set; }
        public Task<EmbeddedMediaRetentionDecision> CheckAsync(EmbeddedMediaRetentionRequest request, CancellationToken ct)
        {
            if (Throw) throw new IOException("synthetic unavailable consumer store");
            return Task.FromResult(Decision);
        }
    }
    private static EmbeddedMediaReferenceRepository References(EmbeddedPhotoProcessingFixture f, Cmsify.Infrastructure.Persistence.CmsifyDbContext db, Guard? guard = null) =>
        new(db, f.Storage, guard is null ? [] : [new RegisteredEmbeddedMediaRetentionGuard("consumer", guard)], f.Time);
    private static async Task<EmbeddedPhotoManifestIdentity> Ready(EmbeddedPhotoProcessingFixture f)
    {
        var asset = await f.Upload(); await using var db = f.Context();
        (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        var manifest = await db.PhotoManifests.SingleAsync(x => x.AssetId == asset, Ct);
        return new(asset, 1, manifest.ManifestSha256);
    }
    private static ProtectEmbeddedMediaReferencesRequest Protect(EmbeddedPhotoProcessingFixture f, params EmbeddedPhotoManifestIdentity[] manifests) =>
        new(f.WorkspaceId, "consumer", Guid.NewGuid(), Guid.NewGuid(), manifests);

    [Fact]
    public async Task OrderedSetReplayAndChangedIntentConflict()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var first = await Ready(f); var second = await Ready(f);
        await using var db = f.Context(); var request = Protect(f, first, second); var repo = References(f, db);
        var protectedSet = await repo.ProtectAsync(request, f.ActorId, Permit, Ct);
        protectedSet.IsSuccess.ShouldBeTrue();
        var replay = await repo.ProtectAsync(request, f.ActorId, Permit, Ct);
        replay.Value.ProtectionReceipt.ShouldBe(protectedSet.Value.ProtectionReceipt);
        (await repo.ProtectAsync(request with { Manifests = [second, first] }, f.ActorId, Permit, Ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        (await db.MediaReferenceSets.CountAsync(Ct)).ShouldBe(1);
        (await db.MediaReferenceEntries.OrderBy(x => x.Ordinal).Select(x => x.AssetId).ToListAsync(Ct)).ShouldBe(new[] { first.AssetId, second.AssetId });
    }

    [Fact]
    public async Task UnknownConsumerCommitNeverExpiresProtection()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        var guard = new Guard(); await using var db = f.Context(); var request = Protect(f, manifest); var repo = References(f, db, guard);
        var before = (await repo.ProtectAsync(request, f.ActorId, Permit, Ct)).Value;
        f.Time.Now += TimeSpan.FromDays(365);
        guard.Throw = true;
        var release = await repo.ReleaseAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey, Guid.NewGuid(), before.ProtectionRevision), f.ActorId, Permit, Ct);
        release.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
        var after = (await repo.GetOutcomeAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey), f.ActorId, Permit, Ct)).Value;
        after.State.ShouldBe(EmbeddedMediaReferenceState.Protected); after.ProtectionRevision.ShouldBe(before.ProtectionRevision);
    }

    [Theory]
    [InlineData(false, EmbeddedMediaRetentionDecision.ReleaseAllowed)]
    [InlineData(true, EmbeddedMediaRetentionDecision.Retain)]
    [InlineData(true, EmbeddedMediaRetentionDecision.Unknown)]
    public async Task MissingOrRetainingGuardDeniesRelease(bool register, EmbeddedMediaRetentionDecision decision)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context(); var request = Protect(f, manifest);
        var repo = References(f, db, register ? new Guard { Decision = decision } : null);
        var before = (await repo.ProtectAsync(request, f.ActorId, Permit, Ct)).Value;
        var result = await repo.ReleaseAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey, Guid.NewGuid(), before.ProtectionRevision), f.ActorId, Permit, Ct);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
        (await db.MediaReferenceSets.AsNoTracking().SingleAsync(Ct)).State.ShouldBe(EmbeddedMediaReferenceState.Protected);
    }

    [Fact]
    public async Task ExactReleaseIsTerminalAndCannotResurrect()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        var guard = new Guard { Decision = EmbeddedMediaRetentionDecision.ReleaseAllowed };
        await using var db = f.Context(); var repo = References(f, db, guard); var request = Protect(f, manifest);
        var before = (await repo.ProtectAsync(request, f.ActorId, Permit, Ct)).Value;
        var release = new ReleaseEmbeddedMediaReferencesRequest(f.WorkspaceId, "consumer", request.ReferenceKey, Guid.NewGuid(), before.ProtectionRevision);
        (await repo.ReleaseAsync(release with { ExpectedProtectionRevision = 2 }, f.ActorId, Permit, Ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        var result = await repo.ReleaseAsync(release, f.ActorId, Permit, Ct);
        result.Value.State.ShouldBe(EmbeddedMediaReferenceState.Released);
        (await repo.ReleaseAsync(release, f.ActorId, Permit, Ct)).Value.ReleaseReceipt.ShouldBe(result.Value.ReleaseReceipt);
        (await repo.ProtectAsync(request, f.ActorId, Permit, Ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        (await repo.ProtectAsync(request with { ReferenceKey = Guid.NewGuid(), OperationKey = Guid.NewGuid() }, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ProtectionRacingDeletionHasOneSafeWinner()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var protecting = f.Context(); await using var deleting = f.Context();
        var asset = await deleting.MediaAssets.AsNoTracking().SingleAsync(Ct);
        var protection = References(f, protecting).ProtectAsync(Protect(f, manifest), f.ActorId, Permit, Ct);
        var delete = new MediaAssetDeletionRepository(deleting, Options.Create(new MediaOperationalOptions()), f.Time)
            .DeleteAsync(new(f.WorkspaceId, manifest.AssetId, asset.UpdatedAt.UtcTicks / 10), f.ActorId, _ => Task.FromResult(true), Ct);
        await Task.WhenAll(protection, delete);
        ((await protection).IsSuccess && (await delete).IsSuccess).ShouldBeFalse();
        ((await protection).IsSuccess || (await delete).IsSuccess).ShouldBeTrue();
        await using var check = f.Context();
        if ((await protection).IsSuccess)
        {
            (await delete).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
            (await check.MediaAssets.SingleAsync(Ct)).BlobState.ShouldBe(Cmsify.Core.Domain.Enums.MediaBlobState.Available);
        }
        else (await check.MediaAssets.IgnoreQueryFilters().SingleAsync(Ct)).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishedAndBackupReferencesSurviveDraftRemoval()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        var guard = new Guard { Decision = EmbeddedMediaRetentionDecision.ReleaseAllowed };
        await using var db = f.Context(); var repo = References(f, db, guard);
        var draft = Protect(f, manifest); var published = Protect(f, manifest); var backup = Protect(f, manifest);
        await repo.ProtectAsync(draft, f.ActorId, Permit, Ct); await repo.ProtectAsync(published, f.ActorId, Permit, Ct); await repo.ProtectAsync(backup, f.ActorId, Permit, Ct);
        (await repo.ReleaseAsync(new(f.WorkspaceId, "consumer", draft.ReferenceKey, Guid.NewGuid(), 1), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        var asset = await db.MediaAssets.AsNoTracking().SingleAsync(Ct);
        (await new MediaAssetDeletionRepository(db, Options.Create(new MediaOperationalOptions()), f.Time)
            .DeleteAsync(new(f.WorkspaceId, manifest.AssetId, asset.UpdatedAt.UtcTicks / 10), f.ActorId, _ => Task.FromResult(true), Ct))
            .Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
        (await db.MediaReferenceSets.CountAsync(x => x.State == EmbeddedMediaReferenceState.Protected, Ct)).ShouldBe(2);
        foreach (var variant in await db.PhotoVariants.ToListAsync(Ct))
            (await f.Storage.Provider.ExistsAsync(variant.StorageKey, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task GuessedAssetReferenceAndVariantIdsDoNotAuthorizeRead()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context(); var request = Protect(f, manifest);
        var reference = (await References(f, db).ProtectAsync(request, f.ActorId, Permit, Ct)).Value;
        var storage = new CountingStorage(f.Storage.Provider);
        var read = new EmbeddedPhotoReadRepository(db, new(storage, "local"));
        var sink = new Sink();
        Task<bool> Deny(EmbeddedPhotoOperation _, CancellationToken token) => Task.FromResult(false);
        (await read.ReadOriginalAsync(new(f.WorkspaceId, manifest.AssetId, sink), f.ActorId, Deny, Ct)).IsFailure.ShouldBeTrue();
        var authority = new EmbeddedPhotoReadAuthority(EmbeddedPhotoReadKind.ConsumerReference, "consumer", request.ReferenceKey, 1);
        (await read.ReadVariantAsync(new(f.WorkspaceId, manifest, "320", authority, sink), f.ActorId, Deny, Ct)).IsFailure.ShouldBeTrue();
        (await read.ReadVariantAsync(new(f.WorkspaceId, manifest, "320", authority with { ReferenceKey = Guid.NewGuid() }, sink), f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
        (await read.ReadVariantAsync(new(Guid.NewGuid(), manifest, "320", authority, sink), f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
        storage.OpenCount.ShouldBe(0); sink.Calls.ShouldBe(0);
        var success = await read.ReadVariantAsync(new(f.WorkspaceId, manifest, "320", authority, sink), f.ActorId, Permit, Ct);
        success.IsSuccess.ShouldBeTrue(); sink.Calls.ShouldBe(1);
        success.Value.ContentType.ShouldBe(EmbeddedPhotoPolicy.WebpContentType);
        sink.Length.ShouldBe(success.Value.Length);
        reference.State.ShouldBe(EmbeddedMediaReferenceState.Protected);
    }

    [Fact]
    public async Task UnavailableVariantsCannotBeProtected()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context();
        var variant = await db.PhotoVariants.SingleAsync(x => x.VariantKey == "640", Ct);
        await f.Storage.Provider.DeleteAsync(variant.StorageKey, Ct);
        var result = await References(f, db).ProtectAsync(Protect(f, manifest), f.ActorId, Permit, Ct);
        result.IsFailure.ShouldBeTrue(); (await db.MediaReferenceSets.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task UnknownReferenceOutcomeIsNotReleased()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await using var db = f.Context();
        var result = await References(f, db).GetOutcomeAsync(new(f.WorkspaceId, "consumer", Guid.NewGuid()), f.ActorId, Permit, Ct);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.NotFound);
    }

    [Fact]
    public async Task ProtectedOriginalBlocksPhysicalDeletionPreparation()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context();
        (await References(f, db).ProtectAsync(Protect(f, manifest), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        db.MediaDeletionIntents.Add(new() { MediaAssetId = manifest.AssetId, Provider = "local", StorageKey = original.StorageKey,
            Reason = "user_delete", NotBefore = f.Time.GetUtcNow(), NextAttemptAt = f.Time.GetUtcNow(), CreatedAt = f.Time.GetUtcNow() });
        await db.SaveChangesAsync(Ct);
        var cleanup = new MediaReconciliationRepository(db);
        var claim = (await cleanup.ClaimDeletionIntentsAsync("test", f.Time.GetUtcNow(), TimeSpan.FromMinutes(1), 1, Ct)).Single();
        (await cleanup.PrepareDeletionAsync(claim, f.Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct))
            .ShouldNotBe(DeletionPreparationResult.Ready);
        (await f.Storage.Provider.ExistsAsync(original.StorageKey, Ct)).ShouldBeTrue();
        (await db.MediaDeletionIntents.SingleAsync(Ct)).CompletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task FencedStagingRetiresWhileCommittedObjectsStayOwned()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context();
        await using var cleanupDb = f.Context();
        var repo = new MediaReconciliationRepository(cleanupDb);
        var pending = new EmbeddedPhotoUploadRepository(db, f.Storage, Options.Create(f.Settings), f.Capability, f.Time);
        var assetId = Guid.NewGuid();
        var begin = await pending.BeginAsync(new(f.WorkspaceId, assetId, Guid.NewGuid(), "phone", 3, f.Settings.PolicyId), f.ActorId, Permit, Ct);
        var source = new PausedSource();
        var write = pending.WriteAsync(new(f.WorkspaceId, begin.Value.SessionId, Guid.NewGuid(), source), f.ActorId, Permit, Ct);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var attempt = await cleanupDb.PhotoUploadAttempts.SingleAsync(x => x.SessionId == begin.Value.SessionId, Ct);
        await repo.FailStaleUploadsAsync(f.Time.Now.AddHours(1), f.Time.Now, 100, Ct);
        (await cleanupDb.PhotoUploadAttempts.SingleAsync(x => x.Id == attempt.Id, Ct)).RetiredAt.ShouldBeNull();
        f.Time.Now += TimeSpan.FromDays(2);
        (await repo.FailStaleUploadsAsync(f.Time.Now.AddDays(-1), f.Time.Now, 100, Ct)).ShouldBe(1);
        (await cleanupDb.PhotoUploadSessions.AsNoTracking().SingleAsync(x => x.Id == begin.Value.SessionId, Ct)).State.ShouldBe(EmbeddedPhotoUploadState.Abandoned);
        source.Resume.TrySetResult();
        (await write).IsFailure.ShouldBeTrue();
        (await repo.StorageKeyExistsAsync("local", attempt.StorageKey, Ct)).ShouldBeFalse();
        foreach (var variant in await db.PhotoVariants.Where(x => x.AssetId == manifest.AssetId).ToListAsync(Ct))
            (await repo.StorageKeyExistsAsync("local", variant.StorageKey, Ct)).ShouldBeTrue();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        (await repo.StorageKeyExistsAsync("local", original.StorageKey, Ct)).ShouldBeTrue();
    }

    [Fact]
    public Task KillAfterProtectionBeforeConsumerCommitRetains() => ReferenceCrash("reference-after-protection");
    [Fact]
    public Task KillAfterReleaseReconcilesTerminalReceipt() => ReferenceCrash("reference-after-release");

    private static ProcessStartInfo Host(EmbeddedPhotoProcessingFixture f, string connection, string stage, Guid asset, Guid reference, Guid operation, string marker)
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/Cmsify.Photo.CrashHost/bin/Release/net10.0/Cmsify.Photo.CrashHost.dll"));
        File.Exists(host).ShouldBeTrue();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { host, stage, connection, f.Root, f.WorkspaceId.ToString(), asset.ToString(),
            reference.ToString(), operation.ToString(), f.ActorId.ToString(), marker }) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task WaitForMarker(Process child, string marker, Task<string> error)
    {
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(marker) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50, Ct);
        File.Exists(marker).ShouldBeTrue(child.HasExited ? "Boundary missing: " + await error : "Boundary timeout");
    }
    private static async Task ReferenceCrash(string stage)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context(); var request = Protect(f, manifest); var operation = Guid.NewGuid();
        if (stage == "reference-after-release")
            (await References(f, db).ProtectAsync(request, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        var marker = Path.Combine(f.Root, stage + ".marker");
        using var child = Process.Start(Host(f, f.Database.ConnectionString, stage,
            manifest.AssetId, request.ReferenceKey, stage == "reference-after-release" ? operation : request.OperationKey, marker))!;
        var output = child.StandardOutput.ReadToEndAsync(Ct); var error = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            await WaitForMarker(child, marker, error);
            child.HasExited.ShouldBeFalse(); child.Kill(true); await child.WaitForExitAsync(Ct); child.ExitCode.ShouldNotBe(0);
            _ = await output; _ = await error;
            f.Time.Now += TimeSpan.FromDays(365);
            await using var restarted = f.Context(); var repo = References(f, restarted);
            var outcome = (await repo.GetOutcomeAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey), f.ActorId, Permit, Ct)).Value;
            outcome.State.ShouldBe(stage == "reference-after-release" ? EmbeddedMediaReferenceState.Released : EmbeddedMediaReferenceState.Protected);
            if (stage == "reference-after-release")
            {
                (await repo.ReleaseAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey, operation, 1), f.ActorId, Permit, Ct))
                    .Value.ReleaseReceipt.ShouldBe(outcome.ReleaseReceipt);
                (await File.ReadAllTextAsync(Path.Combine(f.Root, request.ReferenceKey.ToString("N") + ".intent"), Ct)).ShouldBe("retired");
            }
            else
            {
                (await repo.ReleaseAsync(new(f.WorkspaceId, "consumer", request.ReferenceKey, operation, 1), f.ActorId, Permit, Ct))
                    .Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
                await new MediaReconciliationRepository(restarted).FailStaleUploadsAsync(f.Time.Now.AddDays(-1), f.Time.Now, 100, Ct);
                (await restarted.MediaReferenceSets.SingleAsync(Ct)).State.ShouldBe(EmbeddedMediaReferenceState.Protected);
            }
            foreach (var variant in await restarted.PhotoVariants.ToListAsync(Ct))
                (await f.Storage.Provider.ExistsAsync(variant.StorageKey, Ct)).ShouldBeTrue();
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task SeparateProcessesProtectionAndDeletionHaveOneSafeWinner()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var manifest = await Ready(f);
        await using var db = f.Context(); var connection = f.Database.ConnectionString;
        var firstMarker = Path.Combine(f.Root, "protect-race.marker"); var secondMarker = Path.Combine(f.Root, "delete-race.marker");
        using var protect = Process.Start(Host(f, connection, "reference-race-protect", manifest.AssetId, Guid.NewGuid(), Guid.NewGuid(), firstMarker))!;
        using var delete = Process.Start(Host(f, connection, "reference-race-delete", manifest.AssetId, Guid.NewGuid(), Guid.NewGuid(), secondMarker))!;
        var firstOutput = protect.StandardOutput.ReadToEndAsync(Ct); var firstError = protect.StandardError.ReadToEndAsync(Ct);
        var secondOutput = delete.StandardOutput.ReadToEndAsync(Ct); var secondError = delete.StandardError.ReadToEndAsync(Ct);
        try
        {
            await Task.WhenAll(WaitForMarker(protect, firstMarker, firstError), WaitForMarker(delete, secondMarker, secondError));
            await File.WriteAllTextAsync(Path.Combine(f.Root, "reference-race.go"), "go", Ct);
            await Task.WhenAll(protect.WaitForExitAsync(Ct), delete.WaitForExitAsync(Ct)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            protect.ExitCode.ShouldBe(0, await firstError); delete.ExitCode.ShouldBe(0, await secondError);
            _ = await firstOutput; _ = await secondOutput;
            var results = new[] { await File.ReadAllTextAsync(firstMarker + ".result", Ct), await File.ReadAllTextAsync(secondMarker + ".result", Ct) };
            results.Count(x => x == "success").ShouldBe(1);
            await using var fresh = f.Context();
            var asset = await fresh.MediaAssets.IgnoreQueryFilters().SingleAsync(Ct);
            var protectedCount = await fresh.MediaReferenceSets.CountAsync(x => x.State == EmbeddedMediaReferenceState.Protected, Ct);
            (asset.IsDeleted && protectedCount > 0).ShouldBeFalse();
        }
        finally
        {
            foreach (var child in new[] { protect, delete }) if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(Ct); }
        }
    }

    private sealed class PausedSource : IEmbeddedPhotoByteSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return new MemoryStream([1,2,3]);
        }
    }

    private sealed class Sink : IEmbeddedPhotoByteSink
    {
        public int Calls { get; private set; }
        public long Length { get; private set; }
        public async Task WriteAsync(Stream source, CancellationToken ct)
        {
            Calls++; var buffer = new byte[8192]; int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0) Length += read;
        }
    }
    private sealed class CountingStorage(IStorageProvider inner) : IStorageProvider
    {
        public int OpenCount { get; private set; }
        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken ct = default) { OpenCount++; return inner.ReadAsync(key, ct); }
        public Task<StoredObject> StoreAsync(StoreObjectRequest r, CancellationToken ct = default) => inner.StoreAsync(r, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken ct = default) => inner.GetMetadataAsync(key, ct);
        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest r, CancellationToken ct = default) => inner.ListAsync(r, ct);
        public Task<string> GetAccessUrlAsync(string key, TimeSpan? expiry = null, CancellationToken ct = default) => inner.GetAccessUrlAsync(key, expiry, ct);
    }
}
