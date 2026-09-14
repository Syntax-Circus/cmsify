namespace SyntaxCircus.Cmsify.Components.Client;

public static class ContentEditSupport
{
    public static async Task LoadPickListsAsync(CmsifyClient client, Guid workspaceId, IEnumerable<TemplateFieldResponse> fields, Dictionary<Guid, PickListResponse> into)
    {
        var bindings = fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => PickListFieldBinding.FromFieldConfig(field.FieldConfig))
            .Where(binding => binding.PickListId.HasValue && binding.RevisionId.HasValue)
            .Select(binding => (binding.PickListId!.Value, binding.RevisionId!.Value))
            .Distinct()
            .Where(binding => !into.ContainsKey(binding.Item2))
            .ToList();

        // Fire every distinct pick-list revision lookup concurrently rather than one at a time.
        var lookups = bindings
            .Select(binding => (RevisionId: binding.Item2, Task: client.PickLists.GetRevisionAsync(workspaceId, binding.Item1, binding.Item2)))
            .ToList();

        try
        {
            await Task.WhenAll(lookups.Select(l => l.Task));
        }
        catch (CmsifyApiException)
        {
        }

        foreach (var (revisionId, task) in lookups)
        {
            try
            {
                var pickList = await task;
                if (pickList is not null)
                {
                    into[revisionId] = pickList;
                }
            }
            catch (CmsifyApiException)
            {
            }
        }
    }

    public static async Task LoadReferenceOptionsAsync(CmsifyClient client, Guid workspaceId, IEnumerable<TemplateFieldResponse> fields, Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> into)
    {
        var templateIds = fields
            .SelectMany(field => field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value)
                .Concat(field.TemplateId.HasValue ? [field.TemplateId.Value] : []))
            .Distinct()
            .Where(templateId => !into.ContainsKey(templateId))
            .ToList();

        // Every distinct referenced template's option list is independent of the others - fetch
        // them all concurrently instead of one at a time.
        var lookups = templateIds
            .Select(templateId => (TemplateId: templateId, Task: client.Content.ListAsync(workspaceId, null, templateId, null, null, null)))
            .ToList();
        await Task.WhenAll(lookups.Select(l => l.Task));

        foreach (var (templateId, task) in lookups)
        {
            into[templateId] = task.Result?.Items ?? [];
        }
    }
}
