using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoSchemaTests
{
    [Fact]
    public async Task PopulatedLegacyPhotosUpgradeAndReplayWithoutModeReinterpretation()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        var asset = await f.Upload();
        Guid pendingAsset = Guid.NewGuid(), pendingKey = Guid.NewGuid();
        BeginEmbeddedPhotoUploadRequest completedRequest;
        Guid receiptId, completedSession, jobId;
        var pendingRequest = new BeginEmbeddedPhotoUploadRequest(f.WorkspaceId, pendingAsset,
            pendingKey, "legacy-pending.heic", 3, f.Settings.PolicyId);
        await using (var seed = f.Context())
        {
            var session = await seed.PhotoUploadSessions.SingleAsync(x => x.AssetId == asset, Ct);
            completedRequest = new(f.WorkspaceId, asset, session.OperationKey, session.DisplayFileName,
                session.DeclaredLength, session.PolicyId);
            completedSession = session.Id;
            receiptId = (await seed.PhotoUploadReceipts.SingleAsync(Ct)).Id;
            jobId = (await seed.PhotoJobs.SingleAsync(Ct)).Id;
            var repository = new EmbeddedPhotoUploadRepository(seed, f.Storage, Options.Create(f.Settings), f.Capability, f.Time);
            (await repository.BeginAsync(pendingRequest, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        }
        // Remove only the amendment's additive schema in this owned disposable database,
        // retaining actual qualified-mode sessions, receipt, original, revision and job.
        await using (var old = f.Context())
        {
            await old.GetService<IMigrator>().MigrateAsync("20261010162757_AddEmbeddedPhotoFoundation", Ct);
            (await old.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_name = 'embedded_photo_upload_sessions' AND column_name IN ('upload_only', 'expected_source_sha256')").SingleAsync(Ct)).ShouldBe(0);
        }
        await using var upgraded = f.Context();
        await upgraded.Database.MigrateAsync(Ct);
        var applied = (await upgraded.Database.GetAppliedMigrationsAsync(Ct)).ToArray();
        await upgraded.Database.MigrateAsync(Ct);
        (await upgraded.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(applied);
        var sessions = await upgraded.PhotoUploadSessions.AsNoTracking().ToArrayAsync(Ct);
        sessions.Length.ShouldBe(2);
        sessions.ShouldAllBe(x => !x.UploadOnly && x.ExpectedSourceSha256 == null && x.ProcessorVersion == "synthetic-only-1");
        var replay = new EmbeddedPhotoUploadRepository(upgraded, f.Storage, Options.Create(f.Settings), f.Capability, f.Time);
        var begun = await replay.BeginAsync(completedRequest, f.ActorId, Permit, Ct);
        begun.IsSuccess.ShouldBeTrue(); begun.Value.SessionId.ShouldBe(completedSession);
        var recovered = await replay.GetIntentAsync(new(f.WorkspaceId, asset, completedRequest.OperationKey), f.ActorId, Permit, Ct);
        recovered.Value.Receipt!.ReceiptId.ShouldBe(receiptId);
        var pending = await replay.BeginAsync(pendingRequest, f.ActorId, Permit, Ct);
        pending.IsSuccess.ShouldBeTrue();
        var completed = await replay.WriteAsync(new(f.WorkspaceId, pending.Value.SessionId, Guid.NewGuid(), new Bytes([1,2,3])), f.ActorId, Permit, Ct);
        completed.IsSuccess.ShouldBeTrue();
        (await upgraded.PhotoJobs.AnyAsync(x => x.Id == jobId, Ct)).ShouldBeTrue();
        (await upgraded.PhotoJobs.CountAsync(Ct)).ShouldBe(2);
        (await upgraded.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(2);
        (await upgraded.PhotoRevisions.ToArrayAsync(Ct)).ShouldAllBe(x => x.State == EmbeddedPhotoState.Queued && x.ProcessorVersion == "synthetic-only-1");
        (await upgraded.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM embedded_photo_intake_retentions").SingleAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task FreshUpgradeRepeatKeepsLegacyMediaUnqualified()
    {
        await using var fixture = await EmbeddedPhotoFixture.Create("20261008130438_AddEmbeddedContentReceipts");
        var asset = fixture.Asset("cmsify/media/legacy");
        await using (var old = fixture.Context()) { old.MediaAssets.Add(asset); await old.SaveChangesAsync(EmbeddedPhotoFixture.Ct); }
        await using var db = fixture.Context();
        await db.Database.MigrateAsync(EmbeddedPhotoFixture.Ct);
        var first = (await db.Database.GetAppliedMigrationsAsync(EmbeddedPhotoFixture.Ct)).ToArray();
        first.ShouldContain(x => x.EndsWith("AddEmbeddedPhotoFoundation", StringComparison.Ordinal));
        await db.Database.MigrateAsync(EmbeddedPhotoFixture.Ct);
        (await db.Database.GetAppliedMigrationsAsync(EmbeddedPhotoFixture.Ct)).ShouldBe(first);
        (await db.PhotoRevisions.CountAsync(EmbeddedPhotoFixture.Ct)).ShouldBe(0);
        (await db.MediaAssets.SingleAsync(x => x.Id == asset.Id, EmbeddedPhotoFixture.Ct)).BlobState
            .ShouldBe(Cmsify.Core.Domain.Enums.MediaBlobState.Available);
    }

    [Fact]
    public async Task LogicalJobAndOperationIntentAreUnique()
    {
        await using var fixture = await EmbeddedPhotoFixture.Create();
        await using var db = fixture.Context();
        var asset = fixture.Asset("cmsify/media/source");
        db.MediaAssets.Add(asset);
        var session = fixture.Session(asset);
        db.PhotoUploadSessions.Add(session);
        await db.SaveChangesAsync(EmbeddedPhotoFixture.Ct);
        db.PhotoUploadSessions.Add(fixture.Session(asset, session.OperationKey));
        var duplicate = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(EmbeddedPhotoFixture.Ct));
        ((PostgresException)duplicate.InnerException!).SqlState.ShouldBe("23505");
        db.ChangeTracker.Clear();
        db.PhotoRevisions.Add(new() { AssetId = asset.Id, WorkspaceId = fixture.Workspace.Id, Revision = 1,
            SourceSha256 = new('a', 64), PolicyId = "test-policy", ProcessorVersion = "test-processor" });
        db.PhotoJobs.Add(Job(asset.Id, fixture.Workspace.Id));
        await db.SaveChangesAsync(EmbeddedPhotoFixture.Ct);
        db.PhotoJobs.Add(Job(asset.Id, fixture.Workspace.Id));
        var duplicateJob = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(EmbeddedPhotoFixture.Ct));
        ((PostgresException)duplicateJob.InnerException!).SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task NewPrefixesAreNotOrphans()
    {
        await using var fixture = await EmbeddedPhotoFixture.Create();
        await using var db = fixture.Context();
        var asset = fixture.Asset("cmsify/media/asset");
        db.MediaAssets.Add(asset);
        var session = fixture.Session(asset);
        db.PhotoUploadSessions.Add(session);
        db.PhotoUploadAttempts.Add(new() { SessionId = session.Id, WorkspaceId = fixture.Workspace.Id, Generation = 1,
            Token = Guid.NewGuid(), OwnerId = Guid.NewGuid(), StorageProvider = "local", StorageKey = "cmsify/media/photos/attempt" });
        db.PhotoOriginals.Add(new() { AssetId = asset.Id, WorkspaceId = fixture.Workspace.Id, SessionId = session.Id,
            StorageProvider = "local", StorageKey = "cmsify/media/photos/original", Length = 99, SourceSha256 = new('a', 64) });
        db.PhotoRevisions.Add(new() { AssetId = asset.Id, WorkspaceId = fixture.Workspace.Id, Revision = 1,
            SourceSha256 = new('a', 64), PolicyId = "test-policy", ProcessorVersion = "test-processor", State = EmbeddedPhotoState.Ready });
        var job = Job(asset.Id, fixture.Workspace.Id);
        db.PhotoJobs.Add(job);
        db.PhotoProcessingAttempts.Add(new() { JobId = job.Id, WorkspaceId = fixture.Workspace.Id,
            Token = Guid.NewGuid(), VariantKey = "320", StorageProvider = "local", StorageKey = "cmsify/media/photos/processing" });
        db.PhotoManifests.Add(new() { AssetId = asset.Id, WorkspaceId = fixture.Workspace.Id, Revision = 1,
            ManifestSha256 = new('b', 64), SourceSha256 = new('a', 64), PolicyId = "test-policy", ProcessorVersion = "test-processor" });
        db.PhotoVariants.Add(new() { AssetId = asset.Id, WorkspaceId = fixture.Workspace.Id, Revision = 1, VariantKey = "320",
            StorageProvider = "local", StorageKey = "cmsify/media/photos/variant", Length = 99, Sha256 = new('c', 64), Width = 320, Height = 240 });
        await db.SaveChangesAsync(EmbeddedPhotoFixture.Ct);
        var repository = new MediaReconciliationRepository(db);
        foreach (var key in new[] { "attempt", "original", "variant", "processing" })
        {
            (await repository.StorageKeyExistsAsync("local", "cmsify/media/photos/" + key, EmbeddedPhotoFixture.Ct)).ShouldBeTrue();
            await repository.EnqueueOrphanDeletionAsync("local", "cmsify/media/photos/" + key, DateTimeOffset.UtcNow, EmbeddedPhotoFixture.Ct);
        }
        var now = DateTimeOffset.UtcNow;
        var claims = await repository.ClaimDeletionIntentsAsync("photo-test", now, TimeSpan.FromMinutes(1), 10, EmbeddedPhotoFixture.Ct);
        claims.Count.ShouldBe(4);
        foreach (var claim in claims)
            (await repository.PrepareDeletionAsync(claim, now, TimeSpan.FromMinutes(1), EmbeddedPhotoFixture.Ct))
                .ShouldBe(DeletionPreparationResult.Owned);
        (await repository.StorageKeyExistsAsync("local", "cmsify/media/photos/unknown", EmbeddedPhotoFixture.Ct)).ShouldBeFalse();
    }

    private static PhotoJobRecord Job(Guid assetId, Guid workspaceId) => new()
    {
        AssetId = assetId, WorkspaceId = workspaceId, Revision = 1, SourceSha256 = new('a', 64),
        PolicyId = "test-policy", ProcessorVersion = "test-processor"
    };
}
