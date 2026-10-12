using Cmsify.Core.EmbeddedFiles;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using SyntaxCircus.Storage;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileRetentionTests
{
    [Fact]
    public async Task GenericCreationCannotAdoptSessionOwnedOrdinaryKey()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var asset=await h.Db.MediaAssets.SingleAsync(x=>x.Id==session.AssetId,ct);
        asset.StorageKey="ordinary/private.pdf";asset.TransitionBlobState(MediaBlobState.Available,DateTimeOffset.UtcNow);await h.Db.SaveChangesAsync(ct);
        await h.Provider.StoreAsync(new StoreObjectRequest(asset.StorageKey,new MemoryStream(EmbeddedFileFixture.Pdf),EmbeddedFileRules.MediaType),ct);
        var generic=new MediaAssetRepository(h.Db,new CurrentActorInfo(h.ActorId,null,UserRole.Admin,h.WorkspaceId,true),h.Provider,Options.Create(new MediaOperationalOptions()),new ConfigurationBuilder().Build());
        await Should.ThrowAsync<InvalidOperationException>(()=>generic.CreateAsync(new(h.WorkspaceId,"alias.pdf",EmbeddedFileRules.MediaType,EmbeddedFileFixture.Pdf.Length,asset.StorageKey,"local",null),ct));
    }
    [Fact]
    public async Task RetirementAndTwoFileProtectionUseOneAssetLockOrder()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var a=Guid.Parse("00000000-0000-0000-0000-000000000001");var b=Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var identities=new List<EmbeddedFileIdentity>();
        foreach(var asset in new[] {b,a})
        {
            var begin=h.Begin() with { AssetId=asset };var session=(await h.Repository().BeginAsync(begin,h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
            var key=Guid.NewGuid();await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,key,new EmbeddedFileFixture.Source([1,2,3])),h.ActorId,EmbeddedFileFixture.Permit,ct);
            var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,key,new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
            identities.Add(new(asset,receipt.ReceiptId,receipt.Length,receipt.SourceSha256));
        }
        var lockedA=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> PauseAfterA(EmbeddedFileOperation operation,CancellationToken token)
        { if(operation.AssetId==a){lockedA.TrySetResult();await resume.Task.WaitAsync(token);}return true; }
        await using var protectDb=h.Database.Context();await using var cleanupDb=h.Database.Context();
        var references=new Cmsify.Infrastructure.Persistence.EmbeddedFiles.EmbeddedFileReferenceRepository(protectDb,new(h.Provider,"local"),Options.Create(h.Settings),[new("dog-documents",new PreserveGuard())],TimeProvider.System);
        var protecting=references.ProtectAsync(new(h.WorkspaceId,"dog-documents",Guid.NewGuid(),Guid.NewGuid(),identities),h.ActorId,PauseAfterA,ct);
        await lockedA.Task.WaitAsync(TimeSpan.FromSeconds(10),ct);
        var now=DateTimeOffset.UtcNow;var retiring=new MediaReconciliationRepository(cleanupDb).FailStaleUploadsAsync(now.AddDays(1),now,20,ct);
        try
        {
            await using var monitor=new NpgsqlConnection(h.Database.ConnectionString);await monitor.OpenAsync(ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while(true)
            {
                await using var query=new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%media_assets%'",monitor);
                if(Convert.ToInt64(await query.ExecuteScalarAsync(timeout.Token))>0)break;
                await Task.Delay(25,timeout.Token);
            }
            resume.TrySetResult();await Task.WhenAll(protecting,retiring).WaitAsync(TimeSpan.FromSeconds(15),ct);
            (await protecting).IsSuccess.ShouldBeTrue();
        }
        finally { resume.TrySetResult();try { await Task.WhenAll(protecting,retiring); }catch { } }
    }
    private sealed class PreserveGuard : IEmbeddedFileRetentionGuard
    { public Task<EmbeddedFileRetentionDecision> CheckAsync(EmbeddedFileRetentionRequest request,CancellationToken ct)=>Task.FromResult(new EmbeddedFileRetentionDecision(false)); }
    [Fact]
    public async Task CompletedWinnerDoesNotStarveLaterFailedAttempt()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var winner=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        await h.Repository().WriteAsync(new(h.WorkspaceId,winner.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct);
        var failed=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        await h.Repository().WriteAsync(new(h.WorkspaceId,failed.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source([1,2,3])),h.ActorId,EmbeddedFileFixture.Permit,ct);
        var now=DateTimeOffset.UtcNow;var reconciliation=new MediaReconciliationRepository(h.Db);
        for(var i=0;i<3;i++)await reconciliation.FailStaleUploadsAsync(now.AddDays(1),now,1,ct);
        (await h.Db.FileUploadAttempts.AsNoTracking().SingleAsync(x=>x.SessionId==failed.SessionId,ct)).RetiredAt.ShouldNotBeNull();
        (await h.Db.FileUploadAttempts.AsNoTracking().SingleAsync(x=>x.SessionId==winner.SessionId,ct)).RetiredAt.ShouldBeNull();
    }
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
