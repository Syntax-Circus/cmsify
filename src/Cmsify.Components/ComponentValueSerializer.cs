using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyntaxCircus.Cmsify.Components;

public static class ComponentValueSerializer
{
    private static readonly JsonElement EmptyObject = ParseElement("{}");

    public static ComponentInstanceValue Deserialize(JsonElement json, Guid componentId, IReadOnlyDictionary<Guid, ComponentResponse> schemas)
    {
        var instance = new ComponentInstanceValue();
        if (!schemas.TryGetValue(componentId, out var component) || component.CurrentVersion is null)
        {
            // Graceful degradation: an unresolvable schema (never fetched, deleted, etc.) yields an
            // empty value rather than throwing - Task 3's editor shows a warning for this state.
            return instance;
        }

        var hasProperties = json.ValueKind == JsonValueKind.Object;

        foreach (var field in component.CurrentVersion.Fields)
        {
            var target = instance.GetOrCreate(field.Id);

            // Deliberate tolerance for component-schema version drift: a schema field absent from
            // the JSON (the schema may have changed since this value was captured) decodes to an
            // empty ContentFieldEditorValue rather than throwing. JSON properties with no matching
            // schema field are never visited below, so unknown keys are silently ignored too.
            if (!hasProperties || !json.TryGetProperty(field.Key, out var property))
            {
                continue;
            }

            if (field.NestedComponentId.HasValue)
            {
                var nestedComponentId = field.NestedComponentId.Value;
                var occurrences = property.ValueKind == JsonValueKind.Array
                    ? property.EnumerateArray().ToArray()
                    : [property];

                // Decode each nested occurrence directly into a real ComponentInstanceValue object
                // graph - no intermediate JSON-text round trip, at any nesting depth.
                target.ComponentValues = [.. occurrences.Select(occurrence => Deserialize(occurrence, nestedComponentId, schemas))];
            }
            else
            {
                PrimitiveValueCodec.ApplyFromJson(target, field.PrimitiveType, field.FieldConfig, property);
            }
        }

        return instance;
    }

    public static JsonElement Serialize(ComponentInstanceValue instance, Guid componentId, IReadOnlyDictionary<Guid, ComponentResponse> schemas)
    {
        if (!schemas.TryGetValue(componentId, out var component) || component.CurrentVersion is null)
        {
            return EmptyObject;
        }

        var obj = new JsonObject();
        foreach (var field in component.CurrentVersion.Fields)
        {
            var value = instance.FieldValues.TryGetValue(field.Id, out var existing) ? existing : new ContentFieldEditorValue();
            var node = field.NestedComponentId.HasValue
                ? SerializeNestedProperty(value, field, schemas)
                : PrimitiveValueCodec.ToJson(value, field.PrimitiveType, field.FieldConfig);

            // Media/File (and any other primitive with no value) omit the property entirely rather
            // than writing an explicit JSON null.
            if (node is not null)
            {
                obj[field.Key] = node;
            }
        }

        return ToClonedElement(obj);
    }

    private static JsonNode SerializeNestedProperty(ContentFieldEditorValue value, ComponentFieldResponse field, IReadOnlyDictionary<Guid, ComponentResponse> schemas)
    {
        var nestedComponentId = field.NestedComponentId!.Value;
        var occurrenceElements = value.ComponentValues.Select(nested => Serialize(nested, nestedComponentId, schemas)).ToArray();

        // A bare object when the field allows only a single occurrence, a JSON array otherwise -
        // matches ContentController.ValidateComponentPickListValuesAsync's tolerant array-or-scalar read.
        if (field.MaxOccurrences == 1)
        {
            var element = occurrenceElements.Length > 0 ? occurrenceElements[0] : EmptyObject;
            return JsonNode.Parse(element.GetRawText()) ?? new JsonObject();
        }

        var array = new JsonArray();
        foreach (var element in occurrenceElements)
        {
            array.Add(JsonNode.Parse(element.GetRawText()) ?? new JsonObject());
        }

        return array;
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ToClonedElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
