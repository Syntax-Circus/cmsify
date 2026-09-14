namespace SyntaxCircus.Cmsify.Components;

public static class ComponentSchemaResolver
{
    public static async Task<Dictionary<Guid, ComponentResponse>> ResolveAsync(
        CmsifyClient client, Guid workspaceId, IEnumerable<Guid> rootComponentIds, CancellationToken ct = default)
    {
        var resolved = new Dictionary<Guid, ComponentResponse>();
        var queue = new Queue<Guid>(rootComponentIds);

        while (queue.Count > 0)
        {
            var componentId = queue.Dequeue();
            if (resolved.ContainsKey(componentId))
            {
                // Already resolved (e.g. referenced by more than one root/ancestor) - the result
                // dictionary itself is the visited set, so this also breaks any cyclic reference.
                continue;
            }

            // GetAsync returns null for a genuinely empty response, but the real CmsifyClient throws
            // CmsifyApiException for any non-success status (e.g. a 404 for a deleted/inaccessible
            // component) rather than returning null - both are treated the same way here: an
            // unresolvable schema is a normal, renderable state, not something that should abort
            // resolution of the other ids.
            ComponentResponse? component;
            try
            {
                component = await client.Components.GetAsync(workspaceId, componentId, ct);
            }
            catch (CmsifyApiException)
            {
                continue;
            }

            if (component?.CurrentVersion is null)
            {
                continue;
            }

            resolved[componentId] = component;

            foreach (var nestedId in component.CurrentVersion.Fields.Where(f => f.NestedComponentId.HasValue).Select(f => f.NestedComponentId!.Value))
            {
                queue.Enqueue(nestedId);
            }
        }

        return resolved;
    }
}
