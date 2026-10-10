using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using NSubstitute;
using Shouldly;

namespace Cmsify.Core.Tests;

public sealed class EmbeddedMediaReferenceAuthorizationTests
{
    [Theory]
    [InlineData("anonymous", "authentication-required")]
    [InlineData("subject", "authentication-required")]
    [InlineData("workspace", EmbeddedPhotoErrors.NotFound)]
    [InlineData("resource", EmbeddedPhotoErrors.Forbidden)]
    public async Task MissingAuthorityNeverReachesAnyReferenceOrReadPort(string stage, string code)
    {
        var references = Substitute.For<IEmbeddedMediaReferenceRepository>();
        var reads = Substitute.For<IEmbeddedPhotoReadRepository>();
        var actor = new CurrentActorInfo(stage == "subject" ? null : Guid.NewGuid(), null, UserRole.Editor, null, stage != "anonymous");
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        workspace.CanReadWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        workspace.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stage != "workspace");
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        resource.CanExecuteAsync(Arg.Any<EmbeddedPhotoOperation>(), Arg.Any<CancellationToken>()).Returns(stage != "resource");
        var ct = TestContext.Current.CancellationToken; var ws = Guid.NewGuid(); var key = Guid.NewGuid();
        var manifest = new EmbeddedPhotoManifestIdentity(Guid.NewGuid(), 1, new string('A', 64));
        var sink = Substitute.For<IEmbeddedPhotoByteSink>();
        (await new ProtectEmbeddedMediaReferencesRequestHandler(references, actor, workspace, resource)
            .HandleAsync(new(ws, "consumer", key, Guid.NewGuid(), [manifest]), ct)).Errors[0].Code.ShouldBe(code);
        (await new GetEmbeddedMediaReferenceOutcomeRequestHandler(references, actor, workspace, resource)
            .HandleAsync(new(ws, "consumer", key), ct)).Errors[0].Code.ShouldBe(code);
        (await new ReleaseEmbeddedMediaReferencesRequestHandler(references, actor, workspace, resource)
            .HandleAsync(new(ws, "consumer", key, Guid.NewGuid(), 1), ct)).Errors[0].Code.ShouldBe(code);
        (await new ReadEmbeddedPhotoOriginalRequestHandler(reads, actor, workspace, resource)
            .HandleAsync(new(ws, manifest.AssetId, sink), ct)).Errors[0].Code.ShouldBe(code);
        (await new ReadEmbeddedPhotoVariantRequestHandler(reads, actor, workspace, resource)
            .HandleAsync(new(ws, manifest, "320", new(EmbeddedPhotoReadKind.PrivatePreview), sink), ct)).Errors[0].Code.ShouldBe(code);
        references.ReceivedCalls().ShouldBeEmpty(); reads.ReceivedCalls().ShouldBeEmpty(); sink.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReaderCannotProtectOrRelease()
    {
        var references = Substitute.For<IEmbeddedMediaReferenceRepository>();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Reader, null, true);
        var workspace = Substitute.For<IWorkspaceAuthorizationService>();
        var resource = Substitute.For<IEmbeddedPhotoAuthorizationService>();
        var ct = TestContext.Current.CancellationToken; var ws = Guid.NewGuid(); var key = Guid.NewGuid();
        (await new ProtectEmbeddedMediaReferencesRequestHandler(references, actor, workspace, resource)
            .HandleAsync(new(ws, "consumer", key, Guid.NewGuid(), [new(Guid.NewGuid(), 1, new string('A', 64))]), ct))
            .Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Forbidden);
        (await new ReleaseEmbeddedMediaReferencesRequestHandler(references, actor, workspace, resource)
            .HandleAsync(new(ws, "consumer", key, Guid.NewGuid(), 1), ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Forbidden);
        references.ReceivedCalls().ShouldBeEmpty();
    }
}
