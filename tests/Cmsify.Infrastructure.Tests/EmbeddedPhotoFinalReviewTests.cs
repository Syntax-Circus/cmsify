using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoFinalReviewTests
{
    private static MediaAssetRepository Generic(EmbeddedPhotoProcessingFixture f, Cmsify.Infrastructure.Persistence.CmsifyDbContext db, bool human = false) =>
        new(db, new CurrentActorInfo(human ? f.ActorId : null, human ? null : Guid.NewGuid(), UserRole.Reader, f.WorkspaceId, false),
            f.Storage.Provider, Options.Create(new MediaOperationalOptions()), new ConfigurationBuilder().Build(), f.Time);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericDiscoveryCannotExposePhotoOriginalKeys(bool human)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context(); var repo = Generic(f, db, human);
        (await repo.GetAsync(asset, Ct)).ShouldBeNull();
        (await repo.ListByWorkspaceAsync(f.WorkspaceId, new(), Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AmbiguousAliasesCannotAdoptOrPurgeProtectedPhotoBytes()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context();
        (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        var manifest = await db.PhotoManifests.SingleAsync(Ct);
        var refs = new EmbeddedMediaReferenceRepository(db, f.Storage, [], f.Time);
        (await refs.ProtectAsync(new(f.WorkspaceId, "consumer", Guid.NewGuid(), Guid.NewGuid(),
            [new(asset, 1, manifest.ManifestSha256)]), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        var keys = new List<string> { (await db.PhotoOriginals.SingleAsync(Ct)).StorageKey };
        keys.AddRange(await db.PhotoVariants.Select(x => x.StorageKey).ToListAsync(Ct));
        var repo = Generic(f, db);
        foreach (var key in keys)
        {
            var alias = key.Replace("cmsify/media/", "cmsify//media/");
            (await f.Storage.Provider.GetMetadataAsync(alias, Ct)).ShouldNotBeNull("actual provider resolves this alias");
            await Should.ThrowAsync<InvalidOperationException>(() => repo.CreateAsync(
                new(f.WorkspaceId, "alias", "application/octet-stream", 3, alias, "local", null), Ct));
        }
        // An old/host-inserted alias must also fail physical preparation independently of adoption.
        var aliasKey = keys[0].Replace("cmsify/media/", "cmsify//media/");
        var aliasAsset = new MediaAsset { WorkspaceId = f.WorkspaceId, FileName = "legacy-alias", MimeType = "application/octet-stream",
            StorageKey = aliasKey, StorageProvider = "local", BlobState = MediaBlobState.DeletePending, IsDeleted = true };
        db.MediaAssets.Add(aliasAsset);
        db.MediaDeletionIntents.Add(new() { MediaAssetId = aliasAsset.Id, Provider = "local", StorageKey = aliasKey,
            Reason = "user_delete", NotBefore = f.Time.Now, NextAttemptAt = f.Time.Now });
        await db.SaveChangesAsync(Ct);
        var cleanup = new MediaReconciliationRepository(db);
        var claim = (await cleanup.ClaimDeletionIntentsAsync("review", f.Time.Now, TimeSpan.FromMinutes(1), 1, Ct)).Single();
        (await cleanup.PrepareDeletionAsync(claim, f.Time.Now, TimeSpan.FromMinutes(1), Ct)).ShouldBe(DeletionPreparationResult.Retained);
        foreach (var key in keys) (await f.Storage.Provider.ExistsAsync(key, Ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("deleted", false)]
    [InlineData("processor", false)]
    [InlineData("authority", false)]
    [InlineData("authority", true)]
    public async Task IneligibleFirstPageDoesNotStarveLaterWork(string reason, bool expired)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        await using var db = f.Context(); var originalJob = await db.PhotoJobs.SingleAsync(Ct);
        var blockedAsset = asset;
        if (reason == "deleted")
        {
            var dead = new MediaAsset { WorkspaceId = f.WorkspaceId, FileName = "deleted", MimeType = "application/octet-stream",
                StorageKey = "cmsify/media/photos/deleted", StorageProvider = "local", BlobState = MediaBlobState.DeletePending, IsDeleted = true };
            db.MediaAssets.Add(dead); blockedAsset = dead.Id;
        }
        for (int index = 0; index < 101; index++)
        {
            int revision = index + 2;
            db.PhotoRevisions.Add(new() { AssetId = blockedAsset, WorkspaceId = f.WorkspaceId, Revision = revision,
                OperationKey = Guid.NewGuid(), SourceSha256 = originalJob.SourceSha256, PolicyId = originalJob.PolicyId,
                ProcessorVersion = reason == "processor" ? "unavailable-version" : originalJob.ProcessorVersion });
            db.PhotoJobs.Add(new() { AssetId = blockedAsset, WorkspaceId = f.WorkspaceId, Revision = revision,
                SourceSha256 = originalJob.SourceSha256, PolicyId = originalJob.PolicyId,
                ProcessorVersion = reason == "processor" ? "unavailable-version" : originalJob.ProcessorVersion,
                NextAttemptAt = f.Time.Now.AddMinutes(-1), State = expired ? EmbeddedPhotoState.Processing : EmbeddedPhotoState.Queued,
                AutomaticAttemptCount = expired ? 1 : 0, LeaseExpiresAt = expired ? f.Time.Now.AddMinutes(-1) : null });
        }
        if (expired)
        {
            originalJob.State = EmbeddedPhotoState.Processing; originalJob.AutomaticAttemptCount = 1;
            originalJob.LeaseExpiresAt = f.Time.Now.AddMinutes(-1);
            (await db.PhotoRevisions.SingleAsync(x => x.AssetId == asset && x.Revision == 1, Ct)).State = EmbeddedPhotoState.Processing;
        }
        await db.SaveChangesAsync(Ct);
        var authorization = new RevisionAuthority();
        var repo = new EmbeddedPhotoProcessingRepository(db, f.Storage, Options.Create(f.Settings), f.Capability, authorization, f.Time);
        var worker = new EmbeddedPhotoProcessingProcessor(repo, f.Storage, new CandidateProcessor(), f.Capability, Options.Create(f.Settings), f.Time);
        bool handled = false;
        for (int poll = 0; poll < 4 && !handled; poll++)
        { handled = await worker.ProcessNextAsync(f.ActorId, Ct); f.Time.Now += TimeSpan.FromSeconds(6); }
        handled.ShouldBeTrue();
        (await db.PhotoManifests.SingleAsync(Ct)).AssetId.ShouldBe(asset);
        (await db.PhotoManifests.SingleAsync(Ct)).Revision.ShouldBe(1);
        (await db.PhotoJobs.CountAsync(x => x.Revision > 1 && x.AutomaticAttemptCount != (expired ? 1 : 0), Ct)).ShouldBe(0);
        authorization.Calls.ShouldBeLessThanOrEqualTo(402);
    }

    private sealed class RevisionAuthority : IEmbeddedPhotoProcessingAuthorization
    {
        public int Calls { get; private set; }
        public Task<bool> CanExecuteAsync(Guid actorId, EmbeddedPhotoOperation operation, CancellationToken ct)
        { Calls++; return Task.FromResult(operation.Revision == 1); }
    }
}
