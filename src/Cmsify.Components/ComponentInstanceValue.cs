namespace SyntaxCircus.Cmsify.Components;

public sealed class ComponentInstanceValue
{
    public Dictionary<Guid, ContentFieldEditorValue> FieldValues { get; } = [];

    public ContentFieldEditorValue GetOrCreate(Guid componentFieldId) =>
        FieldValues.TryGetValue(componentFieldId, out var value) ? value : FieldValues[componentFieldId] = new ContentFieldEditorValue();
}
