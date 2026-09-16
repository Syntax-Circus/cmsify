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

    [Fact]
    public async Task ACyclicComponentReferenceTerminatesAndResolvesBothComponentsExactlyOnce()
    {
        // The per-layer parallel resolution still has to break a genuine cycle (A -> B -> A) the same
        // way the original sequential BFS did: the result dictionary IS the visited set, so B's own
        // "nested: A" field is skipped once A is already resolved, instead of looping forever.
        var workspaceId = Guid.NewGuid();
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var aCallCount = 0;
        var bCallCount = 0;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{aId}"))
            {
                aCallCount++;
                return FakeHttpMessageHandler.Json(ComponentJson(aId, workspaceId, nestedComponentId: bId));
            }
            if (path.EndsWith($"/components/{bId}"))
            {
                bCallCount++;
                return FakeHttpMessageHandler.Json(ComponentJson(bId, workspaceId, nestedComponentId: aId));
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [aId], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.ShouldContainKey(aId);
        result.ShouldContainKey(bId);
        aCallCount.ShouldBe(1);
        bCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ASeedPrePopulatesResultAndSkipsRefetchingAlreadyResolvedIds()
    {
        // ContentEditSupport threads an ancestor's already-resolved schemas in as `seed` so a
        // component reused down an Inline hierarchy (or across a parent's own fields and its
        // children's) is fetched once instead of once per level/child.
        var workspaceId = Guid.NewGuid();
        var seededId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var freshCallCount = 0;

        var client = TestCmsifyClientFactory.Create(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{freshId}"))
            {
                freshCallCount++;
                return FakeHttpMessageHandler.Json(ComponentJson(freshId, workspaceId));
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var seed = new Dictionary<Guid, ComponentResponse>
        {
            [seededId] = System.Text.Json.JsonSerializer.Deserialize<ComponentResponse>(ComponentJson(seededId, workspaceId), SyntaxCircus.Cmsify.Contracts.CmsifyJsonOptions.Create())!
        };

        var result = await ComponentSchemaResolver.ResolveAsync(client, workspaceId, [seededId, freshId], TestContext.Current.CancellationToken, seed);

        result.Count.ShouldBe(2);
        result.ShouldContainKey(seededId);
        result.ShouldContainKey(freshId);
        freshCallCount.ShouldBe(1);

        // The seed dictionary itself must be untouched (ResolveAsync only ever reads from it) - a
        // caller sharing one seed across several concurrent sibling loads relies on this.
        seed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvesEveryIdInALayerConcurrentlyRatherThanOneAtATime()
    {
        // Proves the per-layer resolution genuinely fires Task.WhenAll over the whole layer instead
        // of just being correct under a sequential-equivalent ordering - two independent root ids
        // must both be in flight at once, deterministically (via ConcurrencyGate) rather than by
        // racing a fixed delay against wall-clock time.
        var workspaceId = Guid.NewGuid();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var gate = new ConcurrencyGate(requiredConcurrency: 2);

        var client = TestCmsifyClientFactory.CreateWithConcurrencyGate(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith($"/components/{idA}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(idA, workspaceId));
            }
            if (path.EndsWith($"/components/{idB}"))
            {
                return FakeHttpMessageHandler.Json(ComponentJson(idB, workspaceId));
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, gate);

        var resolveTask = ComponentSchemaResolver.ResolveAsync(client, workspaceId, [idA, idB], TestContext.Current.CancellationToken);

        // If the two ids were resolved one at a time, the gate would never see more than one waiting
        // at once. Observing 2 proves they were fired concurrently.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gate.MaxObserved < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        gate.MaxObserved.ShouldBeGreaterThanOrEqualTo(2);

        var result = await resolveTask;
        result.Count.ShouldBe(2);
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
