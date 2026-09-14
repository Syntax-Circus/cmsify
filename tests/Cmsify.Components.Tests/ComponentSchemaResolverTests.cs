using Shouldly;
using Xunit;

namespace SyntaxCircus.Cmsify.Components.Tests;

public sealed class ComponentSchemaResolverTests
{
    [Fact]
    public async Task ResolvesADirectSingleComponentReference()
    {
        var workspaceId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        var callCount = 0;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            callCount++;
            return FakeHttpMessageHandler.Json(ComponentJson(componentId, workspaceId));
        });

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [componentId], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[componentId].Id.ShouldBe(componentId);
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvesNestedComponentsTransitivelyAcrossMultipleLevels()
    {
        var workspaceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var midId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{rootId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(rootId, workspaceId, nestedComponentId: midId));
            }
            if (path.EndsWith($"/components/{midId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(midId, workspaceId, nestedComponentId: leafId));
            }
            if (path.EndsWith($"/components/{leafId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(leafId, workspaceId));
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [rootId], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(3);
        result.ShouldContainKey(rootId);
        result.ShouldContainKey(midId);
        result.ShouldContainKey(leafId);
    }

    [Fact]
    public async Task FetchesAComponentReferencedByTwoRootsOnlyOnce()
    {
        var workspaceId = Guid.NewGuid();
        var rootAId = Guid.NewGuid();
        var rootBId = Guid.NewGuid();
        var sharedId = Guid.NewGuid();
        var sharedCallCount = 0;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{rootAId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(rootAId, workspaceId, nestedComponentId: sharedId));
            }
            if (path.EndsWith($"/components/{rootBId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(rootBId, workspaceId, nestedComponentId: sharedId));
            }
            if (path.EndsWith($"/components/{sharedId}"))
            {
                sharedCallCount++;
                return FakeHttpMessageHandler.Json(ComponentJson(sharedId, workspaceId));
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [rootAId, rootBId], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(3);
        sharedCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedLookupForOneComponentDoesNotPreventOthersFromResolving()
    {
        var workspaceId = Guid.NewGuid();
        var goodId = Guid.NewGuid();
        var missingId = Guid.NewGuid();

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{goodId}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(goodId, workspaceId));
            }
            if (path.EndsWith($"/components/{missingId}"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [goodId, missingId], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result.ShouldContainKey(goodId);
        result.ShouldNotContainKey(missingId);
    }

    private static string ComponentJson(Guid componentId, Guid workspaceId, Guid? nestedComponentId = null) =>
        $$"""
        {
          "id": "{{componentId}}", "workspaceId": "{{workspaceId}}", "name": "Component", "slug": "component",
          "description": null,
          "currentVersion": {
            "id": "{{Guid.NewGuid()}}", "componentId": "{{componentId}}", "versionNumber": 1,
            "status": "Published", "publishedAt": null, "notes": null,
            "fields": [
              {{(nestedComponentId is null
                ? ""
                : $$"""
                  { "id": "{{Guid.NewGuid()}}", "key": "nested", "label": "Nested", "helpText": null,
                    "order": 0, "isRequired": false, "minOccurrences": 0, "maxOccurrences": null,
                    "primitiveType": null, "nestedComponentId": "{{nestedComponentId}}", "fieldConfig": null }
                  """)}}
            ]
          }
        }
        """;
}
