namespace SyntaxCircus.Cmsify.Components;

public static class ComponentFieldAdapter
{
    public static TemplateFieldResponse ToTemplateField(ComponentFieldResponse field) => new(
        field.Id, null, field.Key, field.Label, field.HelpText, field.Order, field.IsRequired,
        field.MinOccurrences, field.MaxOccurrences, false, CompositionMode.Reference,
        field.PrimitiveType, null, [], field.FieldConfig, field.NestedComponentId);
}
