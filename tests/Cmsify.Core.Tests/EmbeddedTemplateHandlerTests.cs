using Cmsify.Core.EmbeddedContent;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Enums;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;
using System.Text.Json;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedTemplateHandlerTests
{
    [Fact]
    public async Task SetupCanceledBeforeRepository()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new EnsureEmbeddedTemplateRequestHandler(
            Substitute.For<IEmbeddedTemplateRepository>(), CurrentActorInfo.Anonymous, new DenyEmbeddedTemplateSetupAuthorizationService())
            .HandleAsync(new(Guid.NewGuid(), null!), cancellation.Token));
    }
    [Theory]
    [InlineData("anonymous", "authentication-required")]
    [InlineData("workspace", "not-found")]
    [InlineData("resource", "forbidden")]
    [InlineData("success", null)]
    public async Task RoleAndWorkspaceDoNotReplaceResourceAuthority(string stage, string? code)
    {
        var repository = Substitute.For<IEmbeddedTemplateRepository>();
        var workspace = Guid.NewGuid();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Reader, null, stage != "anonymous");
        var workspaces = Substitute.For<IWorkspaceAuthorizationService>();
        workspaces.CanReadWorkspaceAsync(workspace, Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        var resources = Substitute.For<IEmbeddedContentAuthorizationService>();
        resources.CanExecuteAsync(Arg.Any<EmbeddedContentOperation>(), Arg.Any<CancellationToken>()).Returns(stage != "resource");
        repository.GetAsync(Arg.Any<GetEmbeddedTemplateRequest>(),
            Arg.Any<Func<EmbeddedContentOperation, CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedTemplateOutput>.Success(new(workspace, "sample.v1", new('a',64), Guid.NewGuid(), Guid.NewGuid(),1,[])));
        var result = await new GetEmbeddedTemplateRequestHandler(repository, actor, workspaces, resources)
            .HandleAsync(new(workspace, "sample.v1", new('a',64)), TestContext.Current.CancellationToken);
        if (code is null) result.IsSuccess.ShouldBeTrue();
        else result.Errors[0].Code.ShouldBe(code);
    }
    [Fact]
    public async Task SetupRequiresExactFingerprint()
    {
        var repository = Substitute.For<IEmbeddedTemplateRepository>();
        var setup = Substitute.For<IEmbeddedTemplateSetupAuthorizationService>();
        var workspace = Guid.NewGuid();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Reader, null, true);
        var contract = new EmbeddedTemplateContract("sample.v1", "Sample", "sample-v1", "name", [
            new("name", "Name", 0, true, 1, 1, PrimitiveType.Text, ValueKind.Text, CompositionMode.Inline,
                false, JsonSerializer.Deserialize<JsonElement>("{\"maxLength\":200}"))]);
        var fingerprint = EmbeddedContractRules.Fingerprint(contract);
        setup.CanEnsureAsync(Arg.Is<EmbeddedTemplateContractScope>(s => s.WorkspaceId == workspace
            && s.ContractKey == contract.ContractKey && s.Fingerprint == fingerprint), Arg.Any<CancellationToken>()).Returns(true);
        var output = new EmbeddedTemplateOutput(workspace, contract.ContractKey, fingerprint, Guid.NewGuid(), Guid.NewGuid(), 1, []);
        repository.EnsureAsync(Arg.Any<EnsureEmbeddedTemplateRequest>(), actor.UserId!.Value,
            Arg.Any<Func<EmbeddedTemplateContractScope, CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Result<EmbeddedTemplateOutput>.Success(output));
        var result = await new EnsureEmbeddedTemplateRequestHandler(repository, actor, setup)
            .HandleAsync(new(workspace, contract), TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(output);
    }
}
