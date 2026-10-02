using System.Net;
using System.Text.Json;
using Shouldly;
using SyntaxCircus.Cmsify.Components.Client;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ContentEditorDataSourceTests
{
    [Theory]
    [InlineData("pick-list")]
    [InlineData("component")]
    [InlineData("reference")]
    public async Task NeutralHelpersPropagateCancellation(string lookup)
    {
        var source = new DirectEditorDataSource();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => LoadAsync(source, lookup, cancellation.Token));
    }

    [Theory]
    [InlineData("pick-list")]
    [InlineData("component")]
    [InlineData("reference")]
    public async Task NeutralHelpersPropagateUnexpectedFailure(string lookup)
    {
        var source = new DirectEditorDataSource { LookupFailure = new InvalidOperationException("lookup failed") };

        var error = await Should.ThrowAsync<InvalidOperationException>(() => LoadAsync(source, lookup, TestContext.Current.CancellationToken));
        error.Message.ShouldBe("lookup failed");
    }

    [Theory]
    [InlineData("pick-list", true)]
    [InlineData("component", true)]
    [InlineData("template", false)]
    [InlineData("templates", false)]
    [InlineData("reference", false)]
    public async Task SdkAdapterPreservesExpectedApiFailurePolicy(string lookup, bool unresolved)
    {
        var source = new CmsifyClientContentEditorDataSource(TestCmsifyClientFactory.Create(_ => new(HttpStatusCode.NotFound)));

        if (unresolved)
        {
            (await LookupAsync(source, lookup, TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        else
        {
            await Should.ThrowAsync<CmsifyApiException>(() => LookupAsync(source, lookup, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("pick-list")]
    [InlineData("component")]
    [InlineData("template")]
    [InlineData("templates")]
    [InlineData("reference")]
    public async Task SdkAdapterDoesNotConvertUnexpectedFailureToEmptySuccess(string lookup)
    {
        var source = new CmsifyClientContentEditorDataSource(TestCmsifyClientFactory.Create(_ => throw new InvalidOperationException("handler failed")));

        await Should.ThrowAsync<InvalidOperationException>(() => LookupAsync(source, lookup, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("pick-list")]
    [InlineData("component")]
    [InlineData("template")]
    [InlineData("templates")]
    [InlineData("reference")]
    public async Task SdkAdapterPassesCancellationToEachLookup(string lookup)
    {
        var source = new CmsifyClientContentEditorDataSource(TestCmsifyClientFactory.Create(_ => FakeHttpMessageHandler.Json("null")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => LookupAsync(source, lookup, cancellation.Token));
    }

    [Fact]
    public async Task DirectComponentGraphSkipsMissingSchemasAndTerminatesCyclesWithoutMutatingSeed()
    {
        var source = new DirectEditorDataSource();
        var rootId = Guid.NewGuid();
        var nestedId = Guid.NewGuid();
        var seeded = TestComponentFactory.Create();
        source.Components[rootId] = TestComponentFactory.Create(id: rootId, currentVersion:
            TestComponentFactory.CreateVersion(componentId: rootId, fields: [TestComponentFactory.CreateField(nestedComponentId: nestedId)]));
        source.Components[nestedId] = TestComponentFactory.Create(id: nestedId, currentVersion:
            TestComponentFactory.CreateVersion(componentId: nestedId, fields: [TestComponentFactory.CreateField(nestedComponentId: rootId)]));
        var seed = new Dictionary<Guid, ComponentResponse> { [seeded.Id] = seeded };

        var schemas = await ComponentSchemaResolver.ResolveAsync(source, source.WorkspaceId,
            [rootId, Guid.NewGuid(), seeded.Id], TestContext.Current.CancellationToken, seed);

        schemas.Keys.ShouldBe([rootId, nestedId, seeded.Id], ignoreOrder: true);
        seed.Keys.ShouldBe([seeded.Id]);
    }

    private static Task LoadAsync(DirectEditorDataSource source, string lookup, CancellationToken cancellationToken) => lookup switch
    {
        "pick-list" => ContentEditSupport.LoadPickListsAsync(source, source.WorkspaceId,
            [TestFieldFactory.Create(primitiveType: PrimitiveType.PickList, fieldConfig:
                JsonSerializer.SerializeToElement(new { picklistId = Guid.NewGuid(), picklistRevisionId = Guid.NewGuid() }))], [], cancellationToken),
        "component" => ComponentSchemaResolver.ResolveAsync(source, source.WorkspaceId, [Guid.NewGuid()], cancellationToken),
        "reference" => ContentEditSupport.LoadReferenceOptionsAsync(source, source.WorkspaceId,
            [TestFieldFactory.Create(templateId: Guid.NewGuid())], [], cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(lookup))
    };

    private static async Task<object?> LookupAsync(IContentEditorDataSource source, string lookup, CancellationToken cancellationToken) => lookup switch
    {
        "pick-list" => await source.GetPickListRevisionAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancellationToken),
        "component" => await source.GetComponentAsync(Guid.NewGuid(), Guid.NewGuid(), cancellationToken),
        "template" => await source.GetTemplateAsync(Guid.NewGuid(), Guid.NewGuid(), cancellationToken),
        "templates" => await source.ListTemplatesAsync(Guid.NewGuid(), cancellationToken),
        "reference" => await source.ListReferenceOptionsAsync(Guid.NewGuid(), Guid.NewGuid(), cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(lookup))
    };
}
