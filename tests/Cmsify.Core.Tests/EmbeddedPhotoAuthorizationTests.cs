using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedPhotoAuthorizationTests
{
    [Fact]
    public async Task IntentLookupRequiresExactResourceAndFreshSubject()
    {
        var repository = Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor = new MutableActor();
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanReadWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>>? authorize = null;
        repository.GetIntentAsync(Arg.Any<GetEmbeddedPhotoUploadIntentRequest>(), Arg.Any<Guid>(),
            Arg.Do<Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>>>(x => authorize = x), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedPhotoUploadOutcome>.Failure(new("sentinel", "sentinel", ResultErrorKind.NotFound)));
        var request = new GetEmbeddedPhotoUploadIntentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await new GetEmbeddedPhotoUploadIntentRequestHandler(repository, actor, workspace, resource)
            .HandleAsync(request, TestContext.Current.CancellationToken);
        authorize.ShouldNotBeNull();
        var exact = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadUploadOutcome, request.WorkspaceId, request.AssetId);
        (await authorize(exact, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await authorize(exact with { AssetId = Guid.NewGuid() }, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await authorize(exact with { Kind = EmbeddedPhotoOperationKind.WriteUpload }, TestContext.Current.CancellationToken)).ShouldBeFalse();
        actor.UserId = Guid.NewGuid();
        (await authorize(exact, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task IntentLookupDenialDoesNotReachRepository()
    {
        var repository = Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor = new CurrentActorInfo(null, null, UserRole.Editor, null, true, true);
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        var result = await new GetEmbeddedPhotoUploadIntentRequestHandler(repository, actor, workspace, resource)
            .HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe("authentication-required");
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingActorCannotWriteBodyOrReadOutcomeEvenWithSuperAdminFlag()
    {
        var repository = Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor = new CurrentActorInfo(null, null, UserRole.Editor, null, true, true);
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanReadWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        var source = Substitute.For<IEmbeddedPhotoByteSource>();
        var token = TestContext.Current.CancellationToken;
        var write = await new WriteEmbeddedPhotoUploadRequestHandler(repository, actor, workspace, resource)
            .HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), source), token);
        write.Errors[0].Code.ShouldBe("authentication-required");
        var outcome = await new GetEmbeddedPhotoUploadOutcomeRequestHandler(repository, actor, workspace, resource)
            .HandleAsync(new(Guid.NewGuid(), Guid.NewGuid()), token);
        outcome.Errors[0].Code.ShouldBe("authentication-required");
        repository.ReceivedCalls().ShouldBeEmpty();
        source.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("anonymous", "authentication-required")]
    [InlineData("subject", "authentication-required")]
    [InlineData("reader", EmbeddedPhotoErrors.Forbidden)]
    [InlineData("workspace", EmbeddedPhotoErrors.NotFound)]
    [InlineData("resource", EmbeddedPhotoErrors.Forbidden)]
    public async Task MissingAuthorityNeverReachesUploadRepository(string stage, string code)
    {
        var repository = Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor = new CurrentActorInfo(stage == "subject" ? null : Guid.NewGuid(), null,
            stage == "reader" ? UserRole.Reader : UserRole.Editor, null, stage != "anonymous");
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        workspace.CanReadWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(stage != "resource");
        var begin = new BeginEmbeddedPhotoUploadRequestHandler(repository, actor, workspace, resource);
        var result = await begin.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "phone.heic", 10, "policy"), TestContext.Current.CancellationToken);
        result.Errors[0].Code.ShouldBe(code);
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task CommitCallbackRechecksWorkspaceAndSubject()
    {
        var repository = Substitute.For<IEmbeddedPhotoUploadRepository>();
        var actor = new MutableActor();
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(true);
        Func<EmbeddedPhotoOperation, CancellationToken, Task<bool>>? authorize = null;
        repository.BeginAsync(Arg.Any<BeginEmbeddedPhotoUploadRequest>(), Arg.Any<Guid>(),
            Arg.Do<Func<EmbeddedPhotoOperation,CancellationToken,Task<bool>>>(x => authorize = x), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedPhotoUploadOutcome>.Failure(new("sentinel", "sentinel", ResultErrorKind.Validation)));
        var request = new BeginEmbeddedPhotoUploadRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "phone", 3, "policy");
        await new BeginEmbeddedPhotoUploadRequestHandler(repository, actor, workspace, resource).HandleAsync(request, TestContext.Current.CancellationToken);
        authorize.ShouldNotBeNull();
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        (await authorize(new(EmbeddedPhotoOperationKind.BeginUpload, request.WorkspaceId, request.AssetId), TestContext.Current.CancellationToken)).ShouldBeFalse();
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        actor.UserId = Guid.NewGuid();
        (await authorize(new(EmbeddedPhotoOperationKind.BeginUpload, request.WorkspaceId, request.AssetId), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private sealed class MutableActor : ICurrentActor
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public Guid? ApiClientId => null;
        public UserRole Role => UserRole.Editor;
        public Guid? WorkspaceId => null;
        public bool IsAuthenticated => true;
        public bool IsSuperAdmin => false;
    }
}
