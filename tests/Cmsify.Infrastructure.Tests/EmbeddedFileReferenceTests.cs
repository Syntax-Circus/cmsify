using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileReferenceTests
{
    [Fact]
    public async Task RevokedFileDeniesReplayOutcomeAndRelease()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var guard=new Guard { Allow=true };var repository=new EmbeddedFileReferenceRepository(h.Db,new(h.Provider,"local"),Options.Create(h.Settings),[new("dog-documents",guard)],TimeProvider.System);
        var request=new ProtectEmbeddedFileReferencesRequest(h.WorkspaceId,"dog-documents",Guid.NewGuid(),Guid.NewGuid(),[new(receipt.AssetId,receipt.ReceiptId,receipt.Length,receipt.SourceSha256)]);
        (await repository.ProtectAsync(request,h.ActorId,EmbeddedFileFixture.Permit,ct)).IsSuccess.ShouldBeTrue();
        Task<bool> Revoked(EmbeddedFileOperation operation,CancellationToken token)=>Task.FromResult(operation.AssetId is null);
        (await repository.ProtectAsync(request,h.ActorId,Revoked,ct)).Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);
        (await repository.GetOutcomeAsync(new(h.WorkspaceId,"dog-documents",request.ReferenceKey),h.ActorId,Revoked,ct)).Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);
        (await repository.ReleaseAsync(new(h.WorkspaceId,"dog-documents",request.ReferenceKey,Guid.NewGuid(),1),h.ActorId,Revoked,ct)).Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);
        (await h.Db.FileReferenceSets.AsNoTracking().SingleAsync(ct)).State.ShouldBe(EmbeddedFileReferenceState.Protected);
    }
    [Fact]
    public async Task ExactIdentityProtectsAndGuardDenialPreservesTheSet()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var session=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,session.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var guard=new Guard();var repository=new EmbeddedFileReferenceRepository(h.Db,new(h.Provider,"local"),Options.Create(h.Settings),[new("dog-documents",guard)],TimeProvider.System);
        var request=new ProtectEmbeddedFileReferencesRequest(h.WorkspaceId,"dog-documents",Guid.NewGuid(),Guid.NewGuid(),[new(receipt.AssetId,receipt.ReceiptId,receipt.Length,receipt.SourceSha256)]);
        var copied=request with { Files=[request.Files[0] with { ReceiptId=Guid.NewGuid() }] };
        (await repository.ProtectAsync(copied,h.ActorId,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
        var outcome=await repository.ProtectAsync(request,h.ActorId,EmbeddedFileFixture.Permit,ct);outcome.IsSuccess.ShouldBeTrue();
        (await repository.ProtectAsync(request,h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Files.ShouldBe(outcome.Value.Files);
        (await repository.ProtectAsync(request with { OperationKey=Guid.NewGuid() },h.ActorId,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
        var release=new ReleaseEmbeddedFileReferencesRequest(h.WorkspaceId,"dog-documents",request.ReferenceKey,Guid.NewGuid(),1);
        (await repository.ReleaseAsync(release,h.ActorId,EmbeddedFileFixture.Permit,ct)).Errors[0].Code.ShouldBe(EmbeddedFileErrors.Retained);
        (await h.Db.FileReferenceSets.AsNoTracking().SingleAsync(ct)).State.ShouldBe(EmbeddedFileReferenceState.Protected);
        guard.Allow=true;
        (await repository.ReleaseAsync(release,h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.State.ShouldBe(EmbeddedFileReferenceState.Released);
        (await repository.ReleaseAsync(release,h.ActorId,EmbeddedFileFixture.Permit,ct)).IsSuccess.ShouldBeTrue();
        (await repository.ReleaseAsync(release with { OperationKey=Guid.NewGuid() },h.ActorId,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
    }
    private sealed class Guard : IEmbeddedFileRetentionGuard
    {
        public bool Allow { get; set; }
        public Task<EmbeddedFileRetentionDecision> CheckAsync(EmbeddedFileRetentionRequest request,CancellationToken ct)=>Task.FromResult(new EmbeddedFileRetentionDecision(Allow));
    }
}
