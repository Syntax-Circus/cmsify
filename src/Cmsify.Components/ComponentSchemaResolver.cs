namespace SyntaxCircus.Cmsify.Components;

public static class ComponentSchemaResolver
{
    // seed, when supplied, pre-populates the result with schemas an ancestor call already resolved
    // (e.g. the parent template's own component fields, or an outer Inline ancestor's) - those ids
    // are skipped entirely instead of being re-fetched. It is only ever read from (copied into this
    // call's own dictionary up front), never mutated, so passing the same seed into several
    // concurrently-running calls (e.g. sibling Inline children loaded via Task.WhenAll) is safe -
    // each call still owns and mutates only its own private dictionary.
    public static async Task<Dictionary<Guid, ComponentResponse>> ResolveAsync(
        CmsifyClient client, Guid workspaceId, IEnumerable<Guid> rootComponentIds, CancellationToken ct = default,
        IReadOnlyDictionary<Guid, ComponentResponse>? seed = null)
    {
        var resolved = seed is null ? new Dictionary<Guid, ComponentResponse>() : new Dictionary<Guid, ComponentResponse>(seed);

        // Resolved one BFS layer at a time instead of one id at a time: every id in a layer is
        // independent of the others, so they're fetched concurrently with Task.WhenAll, and only the
        // ids discovered from THIS layer's results form the next one. `resolved` is still the
        // visited set exactly as before (now also seeded up front) - that's what breaks a cyclic
        // component reference (A -> B -> A) instead of looping forever.
        var layer = rootComponentIds.Distinct().Where(id => !resolved.ContainsKey(id)).ToList();

        while (layer.Count > 0)
        {
            var lookups = layer.Select(id => (Id: id, Task: FetchOrNullAsync(client, workspaceId, id, ct))).ToList();
            await Task.WhenAll(lookups.Select(l => l.Task));

            var nextLayer = new List<Guid>();
            foreach (var (id, task) in lookups)
            {
                var component = await task;
                if (component?.CurrentVersion is null)
                {
                    continue;
                }

                resolved[id] = component;

                foreach (var nestedId in component.CurrentVersion.Fields.Where(f => f.NestedComponentId.HasValue).Select(f => f.NestedComponentId!.Value))
                {
                    if (!resolved.ContainsKey(nestedId))
                    {
                        nextLayer.Add(nestedId);
                    }
                }
            }

            layer = nextLayer.Distinct().ToList();
        }

        return resolved;
    }

    // GetAsync returns null for a genuinely empty response, but the real CmsifyClient throws
    // CmsifyApiException for any non-success status (e.g. a 404 for a deleted/inaccessible
    // component) rather than returning null - both are treated the same way here: an unresolvable
    // schema is a normal, renderable state, not something that should abort resolution of the other
    // ids. Wrapping the fetch per-id like this (rather than around the whole Task.WhenAll) is what
    // lets one failing id fail privately without cancelling the rest of its layer.
    private static async Task<ComponentResponse?> FetchOrNullAsync(CmsifyClient client, Guid workspaceId, Guid componentId, CancellationToken ct)
    {
        try
        {
            return await client.Components.GetAsync(workspaceId, componentId, ct);
        }
        catch (CmsifyApiException)
        {
            return null;
        }
    }
}
