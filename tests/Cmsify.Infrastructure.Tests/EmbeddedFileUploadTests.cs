using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileUploadTests
{
    [Fact]
    public async Task SameIntentReturnsSameSessionAndOneImmutableReceipt()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var begin=h.Begin();var started=await h.Repository().BeginAsync(begin,h.ActorId,EmbeddedFileFixture.Permit,ct);
        started.IsSuccess.ShouldBeTrue();
        (await h.Repository().BeginAsync(begin,h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.SessionId.ShouldBe(started.Value.SessionId);
        (await h.Repository().BeginAsync(begin with { DisplayFileName="changed.pdf" },h.ActorId,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
        var write=new WriteEmbeddedFileUploadRequest(h.WorkspaceId,started.Value.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf));
        var result=await h.Repository().WriteAsync(write,h.ActorId,EmbeddedFileFixture.Permit,ct);
        result.IsSuccess.ShouldBeTrue();result.Value.Receipt!.SourceSha256.ShouldBe(EmbeddedFileFixture.Digest);
        await using var fresh=h.Database.Context();
        var replay=await h.Repository(fresh).WriteAsync(write,h.ActorId,EmbeddedFileFixture.Permit,ct);
        replay.Value.Receipt.ShouldBe(result.Value.Receipt);
        (await fresh.FileUploadReceipts.CountAsync(ct)).ShouldBe(1);
        (await fresh.PhotoUploadSessions.CountAsync(ct)).ShouldBe(0);
    }
    [Fact]
    public async Task WrongDigestAndPartialBytesNeverComplete()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var started=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var result=await h.Repository().WriteAsync(new(h.WorkspaceId,started.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source([1,2,3])),h.ActorId,EmbeddedFileFixture.Permit,ct);
        result.Errors[0].Code.ShouldBe(EmbeddedFileErrors.InvalidBytes);
        (await h.Db.FileUploadReceipts.CountAsync(ct)).ShouldBe(0);
        var outcome=await h.Repository().GetOutcomeAsync(new(h.WorkspaceId,started.SessionId),h.ActorId,EmbeddedFileFixture.Permit,ct);
        outcome.Value.Receipt.ShouldBeNull();
        (await h.Db.FileUploadSessions.CountAsync(ct)).ShouldBe(1);
    }
    [Fact]
    public async Task ConcurrentWritersYieldOneReceipt()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var started=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var key=Guid.NewGuid();await using var first=h.Database.Context();await using var second=h.Database.Context();
        var request=new WriteEmbeddedFileUploadRequest(h.WorkspaceId,started.SessionId,key,new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf));
        var results=await Task.WhenAll(h.Repository(first).WriteAsync(request,h.ActorId,EmbeddedFileFixture.Permit,ct),h.Repository(second).WriteAsync(request,h.ActorId,EmbeddedFileFixture.Permit,ct));
        results.Any(x=>x.IsSuccess).ShouldBeTrue();
        (await h.Db.FileUploadReceipts.CountAsync(ct)).ShouldBe(1);
    }
}
