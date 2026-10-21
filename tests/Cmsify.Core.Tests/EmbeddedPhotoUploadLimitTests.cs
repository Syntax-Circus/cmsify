using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPhotoUploadLimitTests
{
    [Theory]
    [InlineData(20L * 1024 * 1024 + 1)]
    [InlineData(64L * 1024 * 1024)]
    public async Task ExplicitLargerPolicyReachesNormalAuthorizedRepository(long length)
    {
        var (repository,actor,workspace,resource)=Services();
        repository.BeginAsync(Arg.Any<BeginEmbeddedPhotoUploadRequest>(),Arg.Any<Guid>(),Arg.Any<Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>>>(),Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedPhotoUploadOutcome>.Failure(new("repository-sentinel","Reached authorized repository",ResultErrorKind.NotFound)));
        var result=await new BeginEmbeddedPhotoUploadRequestHandler(repository,actor,workspace,resource,new EmbeddedPhotoUploadLimits(64L*1024*1024))
            .HandleAsync(Request(length),TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("repository-sentinel");
    }
    [Fact]
    public async Task ExistingConstructorRetainsDefaultTwentyMiBRejection()
    {
        var (repository,actor,workspace,resource)=Services();
        var result=await new BeginEmbeddedPhotoUploadRequestHandler(repository,actor,workspace,resource)
            .HandleAsync(Request(20L*1024*1024+1),TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Validation);repository.ReceivedCalls().ShouldBeEmpty();
    }
    [Theory]
    [InlineData(0,1)]
    [InlineData(64L*1024*1024+1,1)]
    [InlineData(64L*1024*1024,64L*1024*1024+1)]
    public async Task InvalidPolicyOrOversizedOriginalNeverReachesRepository(long limit,long length)
    {
        var (repository,actor,workspace,resource)=Services();
        var result=await new BeginEmbeddedPhotoUploadRequestHandler(repository,actor,workspace,resource,new EmbeddedPhotoUploadLimits(limit))
            .HandleAsync(Request(length),TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Validation);repository.ReceivedCalls().ShouldBeEmpty();
    }
    private static BeginEmbeddedPhotoUploadRequest Request(long length)=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"source.png",length,"phone-sdr-webp-v1");
    private static (IEmbeddedPhotoUploadRepository,ICurrentActor,IWorkspaceAuthorizationService,IEmbeddedPhotoAuthorizationService) Services()
    {
        var repository=Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor=new CurrentActorInfo(Guid.NewGuid(),null,UserRole.Editor,null,true);
        var workspace=Substitute.For<IWorkspaceAuthorizationService>();workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(),Arg.Any<CancellationToken>()).Returns(true);
        var resource=Substitute.For<IEmbeddedPhotoAuthorizationService>();resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(),Arg.Any<CancellationToken>()).Returns(true);
        return(repository,actor,workspace,resource);
    }
}
