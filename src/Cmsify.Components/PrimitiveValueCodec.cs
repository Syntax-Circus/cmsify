using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyntaxCircus.Cmsify.Components;

public static class PrimitiveValueCodec
{
    public static void ApplyFromJson(ContentFieldEditorValue target, PrimitiveType? primitiveType, JsonElement? fieldConfig, JsonElement value)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    target.BoolValue = value.GetBoolean();
                }

                break;

            case PrimitiveType.PickList:
                var binding = PickListFieldBinding.FromFieldConfig(fieldConfig);
                if (binding.Multiple)
                {
                    target.MultiValues = value.ValueKind == JsonValueKind.Array
                        ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
                        : [];
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    target.TextValue = value.GetString();
                }

                break;

            case PrimitiveType.Media:
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var mediaAssetId))
                {
                    target.FallbackMediaAssetId = mediaAssetId;
                }

                break;

            case PrimitiveType.File:
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var fileAssetId))
                {
                    target.FallbackFileAssetId = fileAssetId;
                }

                break;

            // Text/RichText/Markdown/Link/Quote, plus any unrecognized primitive type: treat as
            // plain text, matching FieldEditor.razor's existing "default: TextFieldEditor" fallback.
            default:
                if (value.ValueKind == JsonValueKind.String)
                {
                    target.TextValue = value.GetString();
                }

                break;
        }
    }

    public static JsonNode? ToJson(ContentFieldEditorValue value, PrimitiveType? primitiveType, JsonElement? fieldConfig)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Boolean:
                return JsonValue.Create(value.BoolValue);

            case PrimitiveType.PickList:
                var binding = PickListFieldBinding.FromFieldConfig(fieldConfig);
                if (binding.Multiple)
                {
                    var array = new JsonArray();
                    foreach (var item in value.MultiValues)
                    {
                        array.Add(JsonValue.Create(item));
                    }

                    return array;
                }

                return value.TextValue is null ? null : JsonValue.Create(value.TextValue);

            // Media/File: encode/decode the asset id as a JSON string, mirroring the nested-PickList
            // string convention. There is no CmsifyClient here to resolve the actual asset - that
            // happens later in ContentEditPanel (Task 3); this only round-trips the id.
            case PrimitiveType.Media:
                var mediaAssetId = value.SelectedMediaAsset?.Id ?? value.FallbackMediaAssetId;
                return mediaAssetId is null ? null : JsonValue.Create(mediaAssetId.Value.ToString());

            case PrimitiveType.File:
                var fileAssetId = value.SelectedFileAsset?.Id ?? value.FallbackFileAssetId;
                return fileAssetId is null ? null : JsonValue.Create(fileAssetId.Value.ToString());

            // Text/RichText/Markdown/Link/Quote, plus any unrecognized primitive type: treat as
            // plain text, matching FieldEditor.razor's existing "default: TextFieldEditor" fallback.
            default:
                return value.TextValue is null ? null : JsonValue.Create(value.TextValue);
        }
    }
}
