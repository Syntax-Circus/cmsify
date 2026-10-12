using Cmsify.Core.EmbeddedFiles;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedFileAuthorizationTests
{
    [Fact]
    public async Task UnauthenticatedSubjectCannotMutateEvenWithPermissiveHostGuard()
    {
        var actor=new CurrentActorInfo(Guid.NewGuid(),null,UserRole.Editor,null,false,false);
        var repository=Substitute.For<IEmbeddedFileUploadRepository>();var guard=Substitute.For<IEmbeddedFileAuthorizationService>();
        guard.CanExecuteAsync(Arg.Any<EmbeddedFileOperation>(),Arg.Any<CancellationToken>()).Returns(true);
        repository.BeginAsync(Arg.Any<BeginEmbeddedFileUploadRequest>(),Arg.Any<Guid>(),Arg.Any<Func<EmbeddedFileOperation,CancellationToken,Task<bool>>>(),Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedFileUploadOutcome>.Failure(new("sentinel","sentinel",ResultErrorKind.NotFound)));
        var result=await new BeginEmbeddedFileUploadRequestHandler(repository,actor,guard).HandleAsync(new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"dogs","a.pdf",10,new string('a',64),"pdf-v1"),TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);repository.ReceivedCalls().ShouldBeEmpty();
    }
    [Fact]
    public async Task AnonymousReadRequiresExactExplicitGuardAndMutationRemainsDenied()
    {
        var actor=new CurrentActorInfo(null,null,UserRole.Reader,null,false,false);
        var guard=Substitute.For<IEmbeddedFileAuthorizationService>();
        var file=new EmbeddedFileIdentity(Guid.NewGuid(),Guid.NewGuid(),10,new string('a',64));
        var workspace=Guid.NewGuid();
        guard.CanExecuteAsync(Arg.Is<EmbeddedFileOperation>(x=>x.Kind==EmbeddedFileOperationKind.ReadFile && x.WorkspaceId==workspace && x.AssetId==file.AssetId && x.ReceiptId==file.ReceiptId && x.ActorId==null),Arg.Any<CancellationToken>()).Returns(true);
        var read=Substitute.For<IEmbeddedFileReadRepository>();
        Func<EmbeddedFileOperation,CancellationToken,Task<bool>>? check=null;
        read.ReadAsync(Arg.Any<ReadEmbeddedFileRequest>(),null,Arg.Do<Func<EmbeddedFileOperation,CancellationToken,Task<bool>>>(x=>check=x),Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedFileReceipt>.Failure(new("sentinel","sentinel",ResultErrorKind.NotFound)));
        var ct=TestContext.Current.CancellationToken;
        await new ReadEmbeddedFileRequestHandler(read,actor,guard).HandleAsync(new(workspace,"dogs",file,Substitute.For<IEmbeddedFileByteSink>()),ct);
        check.ShouldNotBeNull();
        var op=new EmbeddedFileOperation(EmbeddedFileOperationKind.ReadFile,workspace,file.AssetId,ReceiptId:file.ReceiptId,ConsumerNamespace:"dogs");
        (await check(op,ct)).ShouldBeTrue();
        (await check(op with { ReceiptId=Guid.NewGuid() },ct)).ShouldBeFalse();
        (await check(op with { Kind=EmbeddedFileOperationKind.WriteUpload },ct)).ShouldBeFalse();
        var upload=Substitute.For<IEmbeddedFileUploadRepository>();
        var result=await new BeginEmbeddedFileUploadRequestHandler(upload,actor,guard).HandleAsync(new(workspace,Guid.NewGuid(),Guid.NewGuid(),"dogs","a.pdf",10,new string('a',64),"pdf-v1"),ct);
        result.Errors[0].Code.ShouldBe(EmbeddedFileErrors.Denied);
        upload.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task NormalizeAcceptedIntentBeforeAsyncAuthorization()
    {
        var actor=new CurrentActorInfo(Guid.NewGuid(),null,UserRole.Editor,null,true,true);
        var guard=Substitute.For<IEmbeddedFileAuthorizationService>();
        guard.CanExecuteAsync(Arg.Any<EmbeddedFileOperation>(),Arg.Any<CancellationToken>()).Returns(true);
        var repo=Substitute.For<IEmbeddedFileUploadRepository>();
        repo.BeginAsync(Arg.Any<BeginEmbeddedFileUploadRequest>(),Arg.Any<Guid>(),Arg.Any<Func<EmbeddedFileOperation,CancellationToken,Task<bool>>>(),Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedFileUploadOutcome>.Failure(new("sentinel","sentinel",ResultErrorKind.NotFound)));
        var ct=TestContext.Current.CancellationToken;
        await new BeginEmbeddedFileUploadRequestHandler(repo,actor,guard).HandleAsync(new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"dogs","C:\\private\\a.pdf",10,new string('A',64),"pdf-v1"),ct);
        await repo.Received(1).BeginAsync(Arg.Is<BeginEmbeddedFileUploadRequest>(x=>x.DisplayFileName=="a.pdf" && x.ExpectedSourceSha256==new string('a',64)),actor.UserId!.Value,Arg.Any<Func<EmbeddedFileOperation,CancellationToken,Task<bool>>>(),ct);
    }
}
