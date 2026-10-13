using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using SyntaxCircus.Storage;
using Cmsify.Infrastructure.Storage;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoEnrollmentTests
{
    private static CancellationToken Ct => EmbeddedPhotoFixture.Ct;

    [Fact]
    public async Task UploadReadinessAllowsProcessingWithoutRequiringDecoderCapability()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true;
        f.Capability.Available = false;
        await using var db = f.Context();
        var storage = new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = f.Root }));
        var readiness = await new EmbeddedPhotoUploadReadiness(db, storage, Options.Create(f.Settings)).CheckAsync(Ct);
        readiness.Reasons.ShouldBeEmpty();
        readiness.Ready.ShouldBeTrue();
        await f.Upload();
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task RetryOutcomeAndReplayRemainQueuedAfterReadyWithoutWrites()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        var asset = await f.Upload();
        for (var i = 0; i < 3; i++)
        {
            await using var failedDb = f.Context();
            await f.Worker(failedDb, new EmbeddedPhotoProcessingFixture.FailingProcessor()).ProcessNextAsync(f.ActorId, Ct);
            f.Time.Now += TimeSpan.FromSeconds(i == 0 ? 5 : 30);
        }
        await using var db = f.Context();
        var repository = f.Repository(db);
        var request = new RetryEmbeddedPhotoProcessingRequest(f.WorkspaceId, asset, 1, Guid.NewGuid(), 0);
        var retry = await repository.RetryAsync(request, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct);
        retry.IsSuccess.ShouldBeTrue();
        await using (var workerDb = f.Context())
            (await f.Worker(workerDb, new EmbeddedPhotoProcessingFixture.CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        (await repository.GetAsync(new(f.WorkspaceId, asset, 1), f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Ready);
        var query = new GetEmbeddedPhotoOperationOutcomeRequest(f.WorkspaceId, asset, request.OperationKey, EmbeddedPhotoOperationKind.Retry);
        (await repository.GetOperationOutcomeAsync(query, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Value.ShouldBe(retry.Value);
        retry.Value.State.ShouldBe(EmbeddedPhotoState.Queued);
        (await repository.RetryAsync(request, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Value.ShouldBe(retry.Value);
        (await repository.GetOperationOutcomeAsync(query with { AssetId = Guid.NewGuid() }, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await repository.GetOperationOutcomeAsync(query with { Kind = EmbeddedPhotoOperationKind.EnrollUpload }, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await db.PhotoRevisions.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoOperationReceipts.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoManifests.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task PopulatedUploadOnlySchemaUpgradesWithoutChangingOriginalProof()
    {
        await using var f = await EmbeddedPhotoFixture.Create("20261012163607_AddEmbeddedFileReferences");
        await using var db = f.Context();
        var root = Path.Combine(Path.GetTempPath(), "cmsify-enrollment-upgrade-" + Guid.NewGuid().ToString("N"));
        const string storageKey = "legacy-photo/original";
        byte[] bytes = [1,2,3];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "legacy-photo"));
            await File.WriteAllBytesAsync(Path.Combine(root, storageKey), bytes, Ct);
            var asset = f.Asset(storageKey); asset.SizeBytes = bytes.Length;
            var session = f.Session(asset); session.UploadOnly = true; session.State = EmbeddedPhotoUploadState.Completed;
            session.ProcessorVersion = null; session.DeclaredLength = bytes.Length;
            session.PolicyId = "synthetic-photo-v1";
            var receipt = new PhotoUploadReceiptRecord { AssetId = asset.Id, WorkspaceId = f.Workspace.Id,
                SessionId = session.Id, OperationKey = session.OperationKey, SourceSha256 = hash, Length = bytes.Length };
            db.MediaAssets.Add(asset); db.PhotoUploadSessions.Add(session); db.PhotoUploadReceipts.Add(receipt);
            db.PhotoRevisions.Add(new() { AssetId = asset.Id, WorkspaceId = f.Workspace.Id, Revision = 1,
                OperationKey = session.OperationKey, SourceSha256 = hash, PolicyId = session.PolicyId, State = EmbeddedPhotoState.AwaitingProcessing });
            await db.SaveChangesAsync(Ct);
            // Use the old column shape before migrating; the new CLR model includes the pending marker.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO embedded_photo_originals (asset_id,workspace_id,session_id,storage_provider,storage_key,length,source_sha256,created_at) VALUES ({asset.Id},{f.Workspace.Id},{session.Id},{"local"},{storageKey},{bytes.Length},{hash},{DateTimeOffset.UtcNow})", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO embedded_photo_intake_retentions (workspace_id,asset_id,upload_receipt_id,created_at) VALUES ({f.Workspace.Id},{asset.Id},{receipt.Id},{DateTimeOffset.UtcNow})", Ct);
            await db.GetService<IMigrator>().MigrateAsync(null, Ct);
            await db.GetService<IMigrator>().MigrateAsync(null, Ct);
            db.Database.HasPendingModelChanges().ShouldBeFalse();
            var original = await db.PhotoOriginals.SingleAsync(Ct);
            original.ProcessingEnrollmentRevision.ShouldBeNull(); original.SourceSha256.ShouldBe(hash);
            var settings = Options.Create(new Cmsify.Infrastructure.BackgroundServices.EmbeddedPhotoOptions { Enabled = true,
                UploadOnlyEnabled = true, ProcessingEnabled = true, PolicyId = session.PolicyId });
            var storage = new EmbeddedPhotoStorage(new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = root })), "local");
            var repository = new EmbeddedPhotoProcessingRepository(db, storage, settings,
                new EmbeddedPhotoProcessingFixture.TestCapability(), new EmbeddedPhotoProcessingFixture.TestJobAuthorization(), TimeProvider.System);
            (await Enroll(repository, f.Workspace.Id, asset.Id, receipt.Id, hash, Guid.NewGuid(), session.PolicyId, Guid.NewGuid())).IsSuccess.ShouldBeTrue();
            (await db.PhotoOriginals.SingleAsync(Ct)).ProcessingEnrollmentRevision.ShouldBe(2);
            (await File.ReadAllBytesAsync(Path.Combine(root, storageKey), Ct)).ShouldBe(bytes);
            (await db.PhotoUploadReceipts.SingleAsync(Ct)).Id.ShouldBe(receipt.Id);
            (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM embedded_photo_intake_retentions").SingleAsync(Ct)).ShouldBe(1);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OutcomesAreReadOnlyAndEnrollmentReplaySurvivesReady()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.SingleAsync(Ct);
        var key = Guid.NewGuid();
        var repository = f.Repository(db);
        var enrolled = await Enroll(repository, f.WorkspaceId, asset, receipt.Id, original.SourceSha256, key, f.Settings.PolicyId, f.ActorId);
        enrolled.IsSuccess.ShouldBeTrue();
        await using (var workerDb = f.Context())
            (await f.Worker(workerDb, new EmbeddedPhotoProcessingFixture.CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        var ready = await repository.GetAsync(new(f.WorkspaceId, asset, 2), f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct);
        ready.Value.State.ShouldBe(EmbeddedPhotoState.Ready);
        var query = new GetEmbeddedPhotoOperationOutcomeRequest(f.WorkspaceId, asset, key, EmbeddedPhotoOperationKind.EnrollUpload);
        var outcome = await repository.GetOperationOutcomeAsync(query, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct);
        outcome.Value.ShouldBe(enrolled.Value);
        outcome.Value.State.ShouldBe(EmbeddedPhotoState.Queued);
        f.Settings.ProcessingEnabled = false;
        var replay = await Enroll(repository, f.WorkspaceId, asset, receipt.Id, original.SourceSha256, key, f.Settings.PolicyId, f.ActorId);
        replay.Value.ShouldBe(enrolled.Value);
        (await repository.GetOperationOutcomeAsync(query with { AssetId = Guid.NewGuid() }, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await repository.GetOperationOutcomeAsync(query, f.ActorId, (_, _) => Task.FromResult(false), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoManifests.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoOperationReceipts.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ChangedIntentStaleRevisionAndAuthorityLossCannotAllocate()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.SingleAsync(Ct);
        var request = new EnrollEmbeddedPhotoUploadRequest(f.WorkspaceId, asset, receipt.Id, original.SourceSha256, 1, Guid.NewGuid(), f.Settings.PolicyId);
        var repository = f.Repository(db);
        (await repository.CreateRevisionAsync(new(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(0), f.Settings.PolicyId), f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).IsFailure.ShouldBeTrue();
        var checks = 0;
        (await repository.EnrollAsync(request, f.ActorId, (_, _) => Task.FromResult(++checks == 1), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
        (await repository.EnrollAsync(request, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).IsSuccess.ShouldBeTrue();
        (await repository.EnrollAsync(request with { SourceSha256 = new string('b',64) }, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        (await repository.EnrollAsync(request with { OperationKey = Guid.NewGuid() }, f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await repository.CreateRevisionAsync(new(f.WorkspaceId, asset, Guid.NewGuid(), 2, new(1), f.Settings.PolicyId), f.ActorId, EmbeddedPhotoProcessingFixture.Permit, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task CompetingEnrollmentsCreateOnlyOneProcessingRevision()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.SingleAsync(Ct);
        await using var first = f.Context(); await using var second = f.Context();
        var outcomes = await Task.WhenAll(
            Enroll(f.Repository(first), f.WorkspaceId, asset, receipt.Id, original.SourceSha256, Guid.NewGuid(), f.Settings.PolicyId, f.ActorId),
            Enroll(f.Repository(second), f.WorkspaceId, asset, receipt.Id, original.SourceSha256, Guid.NewGuid(), f.Settings.PolicyId, f.ActorId));
        outcomes.Count(result => result.IsSuccess).ShouldBe(1);
        outcomes.Count(result => result.IsFailure).ShouldBe(1);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoRevisions.CountAsync(Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("provider")]
    public async Task UnavailableOriginalCannotEnterProcessing(string unavailable)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.SingleAsync(Ct);
        if (unavailable == "missing") (await db.MediaAssets.SingleAsync(Ct)).TransitionBlobState(MediaBlobState.Missing, f.Time.Now);
        else original.StorageProvider = "unregistered-provider";
        await db.SaveChangesAsync(Ct);
        var result = await Enroll(f.Repository(db), f.WorkspaceId, asset, receipt.Id, original.SourceSha256,
            Guid.NewGuid(), f.Settings.PolicyId, f.ActorId);
        result.IsFailure.ShouldBeTrue();
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
        (await db.PhotoRevisions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task UploadOnlyAndProcessingCoexistWithoutAutomaticEnrollment()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true;
        var asset = await f.Upload();
        await using var db = f.Context();
        (await db.PhotoRevisions.SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.AwaitingProcessing);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
        (await db.PhotoOriginals.SingleAsync(Ct)).AssetId.ShouldBe(asset);
    }

    [Fact]
    public async Task EnrollmentPreservesOriginalAndHistoryAndReplaysOneQueuedRevision()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.AsNoTracking().SingleAsync(Ct);
        var key = Guid.NewGuid();
        var result = await Enroll(f.Repository(db), f.WorkspaceId, asset, receipt.Id, original.SourceSha256, key, f.Settings.PolicyId, f.ActorId);
        result.IsSuccess.ShouldBeTrue(); result.Value.Revision.ShouldBe(2);
        var replay = await Enroll(f.Repository(db), f.WorkspaceId, asset, receipt.Id, original.SourceSha256, key, f.Settings.PolicyId, f.ActorId);
        replay.Value.ShouldBe(result.Value);
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await db.PhotoOperationReceipts.CountAsync(Ct)).ShouldBe(1);
        var revisions = await db.PhotoRevisions.AsNoTracking().OrderBy(x => x.Revision).ToListAsync(Ct);
        revisions.Count.ShouldBe(2); revisions[0].State.ShouldBe(EmbeddedPhotoState.AwaitingProcessing);
        revisions[0].ProcessorVersion.ShouldBeNull(); revisions[1].State.ShouldBe(EmbeddedPhotoState.Queued);
        var retained = await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
        retained.StorageKey.ShouldBe(original.StorageKey); retained.SourceSha256.ShouldBe(original.SourceSha256);
        retained.SessionId.ShouldBe(original.SessionId); retained.Length.ShouldBe(original.Length);
        (await db.PhotoUploadReceipts.SingleAsync(Ct)).Id.ShouldBe(receipt.Id);
        (await db.PhotoUploadSessions.SingleAsync(Ct)).UploadOnly.ShouldBeTrue();
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM embedded_photo_intake_retentions").SingleAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("hash")]
    public async Task ChangedOriginalProofCannotEnroll(string changed)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.UploadOnlyEnabled = true; f.Settings.ProcessingEnabled = false;
        var asset = await f.Upload(); f.Settings.ProcessingEnabled = true;
        await using var db = f.Context();
        var original = await db.PhotoOriginals.SingleAsync(Ct);
        var receipt = await db.PhotoUploadReceipts.SingleAsync(Ct);
        var result = await Enroll(f.Repository(db), f.WorkspaceId, asset,
            changed == "receipt" ? Guid.NewGuid() : receipt.Id,
            changed == "hash" ? new string('a', 64) : original.SourceSha256,
            Guid.NewGuid(), f.Settings.PolicyId, f.ActorId);
        result.IsFailure.ShouldBeTrue();
        (await db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
        (await db.PhotoRevisions.CountAsync(Ct)).ShouldBe(1);
    }

    private static async Task<Result<EmbeddedPhotoStatus>> Enroll(EmbeddedPhotoProcessingRepository repository,
        Guid workspace, Guid asset, Guid receipt, string sha256, Guid key, string policy, Guid actor)
    {
        return await repository.EnrollAsync(new(workspace, asset, receipt, sha256, 1, key, policy), actor,
            EmbeddedPhotoProcessingFixture.Permit, Ct);
    }
}
