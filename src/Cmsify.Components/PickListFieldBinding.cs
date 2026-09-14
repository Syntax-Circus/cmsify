using System.Text.Json;

namespace SyntaxCircus.Cmsify.Components;

public sealed record PickListFieldBinding(Guid? PickListId, Guid? RevisionId, bool Multiple)
{
    public static PickListFieldBinding FromFieldConfig(JsonElement? fieldConfig)
    {
        if (fieldConfig is not { ValueKind: JsonValueKind.Object } config)
        {
            return new PickListFieldBinding(null, null, false);
        }

        Guid? pickListId = null;
        Guid? revisionId = null;
        var multiple = false;

        if (config.TryGetProperty("picklistId", out var pickProp) &&
            pickProp.ValueKind == JsonValueKind.String &&
            Guid.TryParse(pickProp.GetString(), out var parsedPickList))
        {
            pickListId = parsedPickList;
        }

        if (config.TryGetProperty("picklistRevisionId", out var revisionProp) &&
            revisionProp.ValueKind == JsonValueKind.String &&
            Guid.TryParse(revisionProp.GetString(), out var parsedRevision))
        {
            revisionId = parsedRevision;
        }

        if (config.TryGetProperty("multiple", out var multiProp) &&
            multiProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            multiple = multiProp.GetBoolean();
        }

        return new PickListFieldBinding(pickListId, revisionId, multiple);
    }

    public static PickListResponse? Resolve(TemplateFieldResponse field, IReadOnlyDictionary<Guid, PickListResponse> pickListsByRevisionId)
    {
        var binding = FromFieldConfig(field.FieldConfig);
        return binding.RevisionId.HasValue && pickListsByRevisionId.TryGetValue(binding.RevisionId.Value, out var pickList) ? pickList : null;
    }

    public static IReadOnlyList<ContentItemSummaryResponse> ResolveReferenceOptions(
        TemplateFieldResponse field,
        IReadOnlyDictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> referenceOptionsByTemplateId,
        Guid? currentContentId)
    {
        var templateIds = new List<Guid>();
        if (field.TemplateId.HasValue)
        {
            templateIds.Add(field.TemplateId.Value);
        }

        templateIds.AddRange(field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value));

        return templateIds
            .SelectMany(id => referenceOptionsByTemplateId.TryGetValue(id, out var options) ? options : [])
            .Where(option => option.Id != currentContentId)
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .OrderBy(option => option.Slug ?? option.Id.ToString())
            .ToList();
    }
}
