using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Cmsify.Infrastructure.Storage;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedFileReadTests
{
    [Fact]
    public async Task ExactReceiptReadsAndRevokedOrWrongIdentityCannotExposeBytes()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var started=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var uploaded=(await h.Repository().WriteAsync(new(h.WorkspaceId,started.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var repository=new EmbeddedFileReadRepository(h.Db,new EmbeddedFileStorage(h.Provider,"local"),Options.Create(h.Settings));
        var sink=new Sink();var identity=new EmbeddedFileIdentity(uploaded.AssetId,uploaded.ReceiptId,uploaded.Length,uploaded.SourceSha256);
        var request=new ReadEmbeddedFileRequest(h.WorkspaceId,"dog-documents",identity,sink);
        (await repository.ReadAsync(request,null,EmbeddedFileFixture.Permit,ct)).IsSuccess.ShouldBeTrue();
        sink.Bytes.ToArray().ShouldBe(EmbeddedFileFixture.Pdf);
        var denied=new Sink();
        (await repository.ReadAsync(request with { Sink=denied },null,(_,_)=>Task.FromResult(false),ct)).IsFailure.ShouldBeTrue();
        denied.Bytes.Length.ShouldBe(0);
        (await repository.ReadAsync(request with { File=identity with { ReceiptId=Guid.NewGuid() },Sink=denied },null,EmbeddedFileFixture.Permit,ct)).IsFailure.ShouldBeTrue();
        denied.Bytes.Length.ShouldBe(0);
    }
    private sealed class Sink : IEmbeddedFileByteSink
    {
        public MemoryStream Bytes { get; }=new();
        public Task WriteAsync(Stream source,EmbeddedFileReceipt receipt,CancellationToken ct)=>source.CopyToAsync(Bytes,ct);
    }
    [Fact]
    public async Task RevocationDuringSinkStopsTheNextRead()
    {
        await using var h=await EmbeddedFileFixture.Create();var ct=EmbeddedFileFixture.Ct;
        var started=(await h.Repository().BeginAsync(h.Begin(),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value;
        var receipt=(await h.Repository().WriteAsync(new(h.WorkspaceId,started.SessionId,Guid.NewGuid(),new EmbeddedFileFixture.Source(EmbeddedFileFixture.Pdf)),h.ActorId,EmbeddedFileFixture.Permit,ct)).Value.Receipt!;
        var permitted=true;var sink=new RevokingSink(()=>permitted=false);
        var repository=new EmbeddedFileReadRepository(h.Db,new(h.Provider,"local"),Options.Create(h.Settings));
        var result=await repository.ReadAsync(new(h.WorkspaceId,"dog-documents",new(receipt.AssetId,receipt.ReceiptId,receipt.Length,receipt.SourceSha256),sink),null,(_,_)=>Task.FromResult(permitted),ct);
        result.Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);sink.BytesRead.ShouldBe(1);
    }
    private sealed class RevokingSink(Action revoke) : IEmbeddedFileByteSink
    {
        public int BytesRead { get; private set; }
        public async Task WriteAsync(Stream source,EmbeddedFileReceipt receipt,CancellationToken ct)
        {
            var buffer=new byte[1];BytesRead+=await source.ReadAsync(buffer,ct);revoke();
            BytesRead+=await source.ReadAsync(buffer,ct);
        }
    }
}
