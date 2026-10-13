using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPhotoEnrollmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingCustomProcessingRepositoriesRemainSourceCompatible()
    {
        IEmbeddedPhotoProcessingRepository repository = new LegacyRepository();
        var request = new EnrollEmbeddedPhotoUploadRequest(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new string('a',64),1,Guid.NewGuid(),"test-policy");
        (await repository.EnrollAsync(request,Guid.NewGuid(),(_,_)=>Task.FromResult(true),Ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Unavailable);
    }

    [Fact]
    public async Task EnrollmentRequiresExactResourceAndFreshSubject()
    {
        var repository = Substitute.For<IEmbeddedPhotoProcessingRepository>();
        var actor = new Actor();
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>>? authorize = null;
        repository.EnrollAsync(Arg.Any<EnrollEmbeddedPhotoUploadRequest>(), Arg.Any<Guid>(),
            Arg.Do<Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>>>(value => authorize = value), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedPhotoStatus>.Failure(new("sentinel", "sentinel", ResultErrorKind.NotFound)));
        var request = new EnrollEmbeddedPhotoUploadRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), 1, Guid.NewGuid(), "test-policy");
        await new EnrollEmbeddedPhotoUploadRequestHandler(repository, actor, workspace, resource).HandleAsync(request, Ct);
        authorize.ShouldNotBeNull();
        var exact = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.EnrollUpload, request.WorkspaceId, request.AssetId);
        (await authorize(exact, Ct)).ShouldBeTrue();
        (await authorize(exact with { AssetId = Guid.NewGuid() }, Ct)).ShouldBeFalse();
        (await authorize(exact with { WorkspaceId = Guid.NewGuid() }, Ct)).ShouldBeFalse();
        (await authorize(exact with { Kind = EmbeddedPhotoOperationKind.CreateRevision }, Ct)).ShouldBeFalse();
        actor.UserId = Guid.NewGuid();
        (await authorize(exact, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidProofAndDeniedReaderNeverReachEnrollmentRepository()
    {
        var repository = Substitute.For<IEmbeddedPhotoProcessingRepository>();
        var actor = new Actor();
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        var handler = new EnrollEmbeddedPhotoUploadRequestHandler(repository, actor, workspace, resource);
        var request = new EnrollEmbeddedPhotoUploadRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "not-a-digest", 1, Guid.NewGuid(), "test-policy");
        (await handler.HandleAsync(request, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
        actor.Role = UserRole.Reader;
        (await handler.HandleAsync(request with { SourceSha256 = new string('a', 64) }, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        repository.ReceivedCalls().ShouldBeEmpty();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(request, canceled.Token));
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OperationOutcomeRequiresReadAuthorityAndNeverDelegatesToMutation()
    {
        var repository = Substitute.For<IEmbeddedPhotoProcessingRepository>();
        var actor = new Actor { Role = UserRole.Reader };
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanReadWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        repository.GetOperationOutcomeAsync(Arg.Any<GetEmbeddedPhotoOperationOutcomeRequest>(), Arg.Any<Guid>(),
            Arg.Any<Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedPhotoStatus>.Failure(new("sentinel", "sentinel", ResultErrorKind.NotFound)));
        var handler = new GetEmbeddedPhotoOperationOutcomeRequestHandler(repository, actor, workspace, resource);
        var request = new GetEmbeddedPhotoOperationOutcomeRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmbeddedPhotoOperationKind.EnrollUpload);
        (await handler.HandleAsync(request, Ct)).Errors[0].Code.ShouldBe("sentinel");
        (await handler.HandleAsync(request with { Kind = EmbeddedPhotoOperationKind.BeginUpload }, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
        repository.ReceivedCalls().Count().ShouldBe(1);
        ((int)EmbeddedPhotoOperationKind.Process).ShouldBe(12);
        ((int)EmbeddedPhotoOperationKind.EnrollUpload).ShouldBe(13);
    }

    private sealed class Actor : ICurrentActor
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public Guid? ApiClientId => null;
        public UserRole Role { get; set; } = UserRole.Editor;
        public Guid? WorkspaceId => null;
        public bool IsAuthenticated => true;
        public bool IsSuperAdmin => false;
    }

    private sealed class LegacyRepository : IEmbeddedPhotoProcessingRepository
    {
        public Task<Result<EmbeddedPhotoStatus>> GetAsync(GetEmbeddedPhotoRequest request,Guid actor,Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>> authorize,CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<EmbeddedPhotoStatus>> CreateRevisionAsync(CreateEmbeddedPhotoRevisionRequest request,Guid actor,Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>> authorize,CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<EmbeddedPhotoStatus>> RetryAsync(RetryEmbeddedPhotoProcessingRequest request,Guid actor,Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>> authorize,CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<EmbeddedPhotoManifest>> GetManifestAsync(GetEmbeddedPhotoManifestRequest request,Guid actor,Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>> authorize,CancellationToken ct) => throw new NotImplementedException();
    }
}
