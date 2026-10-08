using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;
using Cmsify.Core.Domain.Enums;
using SyntaxCircus.Common;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedContentWriteHandlerTests
{
    [Theory]
    [InlineData("reader", "forbidden")]
    [InlineData("subject", "authentication-required")]
    [InlineData("workspace", "not-found")]
    [InlineData("resource", "forbidden")]
    [InlineData("success", "owned-repository")]
    public async Task WritesRequireEditorWorkspaceSubjectAndResourceAuthority(string stage, string code)
    {
        var actor = new CurrentActorInfo(stage == "subject" ? null : Guid.NewGuid(), null,
            stage == "reader" ? UserRole.Reader : UserRole.Editor, null, true);
        var repository = Substitute.For<IEmbeddedContentRepository>();
        var workspaces = Substitute.For<IWorkspaceAuthorizationService>();
        workspaces.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        var resource = Substitute.For<IEmbeddedContentAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedContentOperation>(), Arg.Any<CancellationToken>()).Returns(stage != "resource");
        repository.CreateEmbeddedContentAsync(Arg.Any<CreateEmbeddedContentRequest>(), Arg.Any<Guid>(),
            Arg.Any<Func<EmbeddedContentOperation,CancellationToken,Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedContentWriteOutput>.Failure(new("owned-repository", "Reached owned repository", ResultErrorKind.Validation)));
        repository.CreateEmbeddedContentVersionAsync(Arg.Any<CreateEmbeddedContentVersionRequest>(), Arg.Any<Guid>(),
            Arg.Any<Func<EmbeddedContentOperation,CancellationToken,Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedContentWriteOutput>.Failure(new("owned-repository", "Reached owned repository", ResultErrorKind.Validation)));
        var workspace = Guid.NewGuid(); var item = Guid.NewGuid(); var template = Guid.NewGuid(); var operation = Guid.NewGuid();
        var fingerprint = new string('a',64); var token = TestContext.Current.CancellationToken;
        var create = await new CreateEmbeddedContentRequestHandler(repository, actor, workspaces, resource)
            .HandleAsync(new(workspace, template, fingerprint, item, operation, []), token);
        create.Errors[0].Code.ShouldBe(code);
        var version = await new CreateEmbeddedContentVersionRequestHandler(repository, actor, workspaces, resource)
            .HandleAsync(new(workspace, item, 1, new(1), template, fingerprint, operation, []), token);
        version.Errors[0].Code.ShouldBe(code);
    }

    [Fact]
    public async Task CancellationBeforeRepositoryPropagates()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var handler = new CreateEmbeddedContentRequestHandler(Substitute.For<IEmbeddedContentRepository>(), CurrentActorInfo.Anonymous,
            Substitute.For<IWorkspaceAuthorizationService>(), new DenyEmbeddedContentAuthorizationService());
        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(new(Guid.NewGuid(),Guid.NewGuid(),new('a',64),Guid.NewGuid(),Guid.NewGuid(),[]), cancellation.Token));
    }
    [Fact]
    public async Task AnonymousCannotCreateItemVersionOrReadReceipt()
    {
        var repository = Substitute.For<IEmbeddedContentRepository>();
        var workspaces = Substitute.For<IWorkspaceAuthorizationService>();
        var resource = new DenyEmbeddedContentAuthorizationService();
        var workspace = Guid.NewGuid(); var item = Guid.NewGuid(); var template = Guid.NewGuid(); var operation = Guid.NewGuid();
        var fingerprint = new string('a',64); var token = TestContext.Current.CancellationToken;
        var create = await new CreateEmbeddedContentRequestHandler(repository, CurrentActorInfo.Anonymous, workspaces, resource)
            .HandleAsync(new(workspace, template, fingerprint, item, operation, []), token);
        create.Errors[0].Code.ShouldBe("authentication-required");
        var version = await new CreateEmbeddedContentVersionRequestHandler(repository, CurrentActorInfo.Anonymous, workspaces, resource)
            .HandleAsync(new(workspace, item, 1, new(1), template, fingerprint, operation, []), token);
        version.Errors[0].Code.ShouldBe("authentication-required");
        var receipt = await new GetEmbeddedContentOperationReceiptRequestHandler(repository, CurrentActorInfo.Anonymous, workspaces, resource)
            .HandleAsync(new(workspace, item, EmbeddedWriteKind.ItemCreate, operation, fingerprint), token);
        receipt.Errors[0].Code.ShouldBe("authentication-required");
    }
}
