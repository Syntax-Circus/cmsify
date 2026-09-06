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
}
