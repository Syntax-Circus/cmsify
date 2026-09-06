namespace SyntaxCircus.Cmsify.Components;

public sealed record FieldEditorRenderContext(
    TemplateFieldResponse Field,
    ContentFieldEditorValue Value,
    EventCallback<ContentFieldEditorValue> ValueChanged);
