using System.Collections.Immutable;

namespace SyntaxCircus.Cmsify.Components;

public partial class ContentEditForm
{
    [Parameter, EditorRequired] public TemplateVersionResponse TemplateVersion { get; set; } = null!;
    [Parameter, EditorRequired] public IDictionary<Guid, ContentFieldEditorValue> FieldValues { get; set; } = new Dictionary<Guid, ContentFieldEditorValue>();
    [Parameter] public EventCallback<(TemplateFieldResponse Field, ContentFieldEditorValue Value)> FieldValueChanged { get; set; }
    [Parameter] public IReadOnlyDictionary<Guid, PickListResponse> PickListsByRevisionId { get; set; } = new Dictionary<Guid, PickListResponse>();
    [Parameter] public IReadOnlyDictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> ReferenceOptionsByTemplateId { get; set; } = new Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>>();
    [Parameter] public IReadOnlyDictionary<Guid, ComponentResponse> ComponentSchemas { get; set; } = new Dictionary<Guid, ComponentResponse>();
    [Parameter] public string? Slug { get; set; }
    [Parameter] public EventCallback<string?> SlugChanged { get; set; }
    [Parameter] public string? Locale { get; set; }
    [Parameter] public EventCallback<string?> LocaleChanged { get; set; }
    [Parameter] public string? Tags { get; set; }
    [Parameter] public EventCallback<string?> TagsChanged { get; set; }
    [Parameter] public string? SlugHelpText { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnMediaPickRequested { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnFilePickRequested { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }
    [Parameter] public Guid? CurrentContentId { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool ShowSaveButton { get; set; } = true;
    [Parameter] public CmsifyClient? Client { get; set; }
    [Parameter] public IContentEditorDataSource? DataSource { get; set; }
    [Parameter] public Guid WorkspaceId { get; set; }
    [Parameter] public IReadOnlySet<Guid> AncestorTemplateIds { get; set; } = ImmutableHashSet<Guid>.Empty;
    [Parameter] public int Depth { get; set; }

    private PickListResponse? ResolvePickList(TemplateFieldResponse field) =>
        PickListFieldBinding.Resolve(field, PickListsByRevisionId);

    private IReadOnlyList<ContentItemSummaryResponse> ResolveReferenceOptions(TemplateFieldResponse field) =>
        PickListFieldBinding.ResolveReferenceOptions(field, ReferenceOptionsByTemplateId, CurrentContentId);

    // Storing (not just returning a throwaway) is essential: OnMediaPickRequested/OnFilePickRequested
    // is a fire-and-forget request that only mutates the ContentFieldEditorValue later, once the user
    // actually picks an asset - by then, re-rendering (e.g. to show the picker) would otherwise call
    // this again and get a brand new, never-stored instance, silently discarding the pick. Mirrors
    // ComponentInstanceValue.GetOrCreate, which stores into its own dictionary for the same reason.
    private ContentFieldEditorValue GetOrCreateValue(Guid fieldId) =>
        FieldValues.TryGetValue(fieldId, out var value) ? value : FieldValues[fieldId] = new ContentFieldEditorValue();
}
