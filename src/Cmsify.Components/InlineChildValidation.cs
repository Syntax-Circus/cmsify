namespace SyntaxCircus.Cmsify.Components;

// Client-side pre-flight for Inline composition saves: occurrence-count checks only (IsRequired /
// MinOccurrences / MaxOccurrences) across the full instance tree, including nested Inline fields
// inside children. The server remains authoritative for everything else (ValueKind, required leaf
// values, etc.) and still surfaces those failures via CmsifyApiException as today - this exists
// purely so a save doesn't fire a burst of child create/update requests only to have the parent's
// own save reject on an occurrence-count violation that could have been caught for free up front.
public static class InlineChildValidation
{
    public static async Task<IReadOnlyList<string>> ValidateAsync(
        CmsifyClient client, Guid workspaceId, TemplateFieldResponse field, IList<InlineChildInstance> instances, CancellationToken ct = default)
    {
        var errors = new List<string>();
        await ValidateFieldAsync(client, workspaceId, field, instances, errors, ct);
        return errors;
    }

    private static async Task ValidateFieldAsync(
        CmsifyClient client, Guid workspaceId, TemplateFieldResponse field, IList<InlineChildInstance> instances, List<string> errors, CancellationToken ct)
    {
        var activeCount = instances.Count(i => !i.MarkedForDeletion);
        var effectiveMin = Math.Max(field.MinOccurrences, field.IsRequired ? 1 : 0);
        if (activeCount < effectiveMin)
        {
            errors.Add($"'{field.Label}' requires at least {effectiveMin} {(effectiveMin == 1 ? "entry" : "entries")}.");
        }
        if (field.MaxOccurrences is { } max && activeCount > max)
        {
            errors.Add($"'{field.Label}' allows at most {max} {(max == 1 ? "entry" : "entries")}.");
        }

        foreach (var instance in instances.Where(i => !i.MarkedForDeletion && i.TemplateId.HasValue))
        {
            TemplateResponse? template;
            try
            {
                template = await client.Templates.GetAsync(workspaceId, instance.TemplateId!.Value, ct);
            }
            catch (CmsifyApiException)
            {
                // Deleted/inaccessible template - the save call for this instance will surface a
                // clearer, more actionable error than a generic pre-flight validation message.
                continue;
            }

            var nestedInlineFields = template?.CurrentVersion?.Fields.Where(f => f.CompositionMode == CompositionMode.Inline) ?? [];
            foreach (var nestedField in nestedInlineFields)
            {
                var nestedInstances = instance.FieldValues.TryGetValue(nestedField.Id, out var value) ? value.ChildInstances : [];
                await ValidateFieldAsync(client, workspaceId, nestedField, nestedInstances, errors, ct);
            }
        }
    }
}
