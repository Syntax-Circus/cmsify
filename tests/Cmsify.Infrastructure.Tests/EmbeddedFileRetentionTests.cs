using Cmsify.Core.EmbeddedFiles;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileRetentionTests
{
    [Fact]
    public async Task GenericRepositoriesCannotReadUpdateDeleteOrCreateReservedFiles()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var repository=new MediaAssetRepository(h.Db,new CurrentActorInfo(h.ActorId,null,UserRole.Admin,h.WorkspaceId,true),h.Provider,Options.Create(new MediaOperationalOptions()),new ConfigurationBuilder().Build());
        (await repository.GetAsync(receipt.AssetId,ct)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(()=>repository.UpdateAsync(new(receipt.AssetId,"changed.pdf","alt"),ct));
        await Should.ThrowAsync<InvalidOperationException>(()=>repository.SoftDeleteAsync(receipt.AssetId,h.ActorId,ct));
        var asset=await h.Db.MediaAssets.AsNoTracking().SingleAsync(x=>x.Id==receipt.AssetId,ct);
        await Should.ThrowAsync<InvalidOperationException>(()=>repository.CreateAsync(new(h.WorkspaceId,"copied.pdf","application/pdf",receipt.Length,asset.StorageKey,"local",null),ct));
        var deletion=new MediaAssetDeletionRepository(h.Db,Options.Create(new MediaOperationalOptions()),TimeProvider.System);
        (await deletion.DeleteAsync(new(h.WorkspaceId,receipt.AssetId,asset.UpdatedAt.UtcTicks),h.ActorId,_=>Task.FromResult(true),ct)).IsFailure.ShouldBeTrue();
        (await h.Db.MediaDeletionIntents.CountAsync(ct)).ShouldBe(0);
    }
    [Fact]
    public async Task LiveAttemptAndRecoveredWinnerDefeatOrphanDeletion()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var attempt=await h.Db.FileUploadAttempts.AsNoTracking().SingleAsync(ct);
        var reconciliation=new MediaReconciliationRepository(h.Db);var now=DateTimeOffset.UtcNow;
        (await reconciliation.StorageKeyExistsAsync("local",attempt.StorageKey,ct)).ShouldBeTrue();
        await reconciliation.FailStaleUploadsAsync(now.AddDays(1),now,20,ct);
        (await h.Db.FileUploadAttempts.AsNoTracking().SingleAsync(ct)).RetiredAt.ShouldBeNull();
        await reconciliation.EnqueueOrphanDeletionAsync("local",attempt.StorageKey,now,ct);
        var claim=(await reconciliation.ClaimDeletionIntentsAsync("file-test",now,TimeSpan.FromMinutes(1),20,ct)).Single();
        (await reconciliation.PrepareDeletionAsync(claim,now,TimeSpan.FromMinutes(1),ct)).ShouldNotBe(DeletionPreparationResult.Ready);
        (await h.Db.FileUploadReceipts.AsNoTracking().SingleAsync(ct)).Id.ShouldBe(receipt.ReceiptId);
    }
    [Fact]
    public async Task ExpiredIncompleteAttemptBecomesCleanupEligible()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        (await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source([1,2,3])),h.ActorId,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
        var now=DateTimeOffset.UtcNow;var repository=new MediaReconciliationRepository(h.Db);
        await repository.FailStaleUploadsAsync(now.AddDays(1),now,20,ct);
        var attempt=await h.Db.FileUploadAttempts.AsNoTracking().SingleAsync(ct);
        attempt.RetiredAt.ShouldNotBeNull();
        (await repository.StorageKeyExistsAsync("local",attempt.StorageKey,ct)).ShouldBeFalse();
    }
}
