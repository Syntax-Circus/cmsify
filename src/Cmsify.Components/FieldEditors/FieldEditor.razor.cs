using System.Collections.Immutable;

namespace SyntaxCircus.Cmsify.Components;

public partial class FieldEditor
{
    [Parameter, EditorRequired] public TemplateFieldResponse Field { get; set; } = null!;
    [Parameter, EditorRequired] public ContentFieldEditorValue Value { get; set; } = null!;
    [Parameter] public EventCallback<ContentFieldEditorValue> ValueChanged { get; set; }
    [Parameter] public PickListResponse? ResolvedPickList { get; set; }
    [Parameter] public IReadOnlyList<ContentItemSummaryResponse> ReferenceOptions { get; set; } = [];
    [Parameter] public IReadOnlyDictionary<Guid, ComponentResponse> ComponentSchemas { get; set; } = new Dictionary<Guid, ComponentResponse>();
    [Parameter] public IReadOnlyDictionary<Guid, PickListResponse> PickListsByRevisionId { get; set; } = new Dictionary<Guid, PickListResponse>();
    [Parameter] public IReadOnlyDictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> ReferenceOptionsByTemplateId { get; set; } = new Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>>();
    [Parameter] public Guid? CurrentContentId { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnMediaPickRequested { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnFilePickRequested { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public CmsifyClient? Client { get; set; }
    [Parameter] public IContentEditorDataSource? DataSource { get; set; }
    [Parameter] public Guid WorkspaceId { get; set; }
    [Parameter] public IReadOnlySet<Guid> AncestorTemplateIds { get; set; } = ImmutableHashSet<Guid>.Empty;
    [Parameter] public int Depth { get; set; }

    private bool IsCompositionField =>
        Field.TemplateId.HasValue || Field.IsOpen || Field.AllowedTypes.Any(allowed => allowed.AllowedTemplateId.HasValue);

    private PickListFieldBinding PickListBinding => PickListFieldBinding.FromFieldConfig(Field.FieldConfig);

    private FieldEditorRenderContext RenderContext => new(Field, Value, ValueChanged, ReadOnly);

    private bool TryGetOverride(out RenderFragment<FieldEditorRenderContext> fragment)
    {
        if (Field.PrimitiveType.HasValue && FieldTemplateOverrides is not null &&
            FieldTemplateOverrides.TryGetValue(Field.PrimitiveType.Value, out var found))
        {
            fragment = found;
            return true;
        }

        fragment = null!;
        return false;
    }

    private Task UpdateAsync(Action<ContentFieldEditorValue> mutate)
    {
        mutate(Value);
        return ValueChanged.InvokeAsync(Value);
    }
}
