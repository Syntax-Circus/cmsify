using System.Collections.Immutable;

namespace SyntaxCircus.Cmsify.Components.Client;

public partial class InlineChildContentEditor
{
    // Either lookup source is optional. Explicit DataSource wins; SDK-only consumers keep
    // their existing behavior, and consumers supplying neither receive a safe warning.
    [Parameter] public CmsifyClient? Client { get; set; }
    [Parameter] public IContentEditorDataSource? DataSource { get; set; }
    [Parameter, EditorRequired] public Guid WorkspaceId { get; set; }
    [Parameter, EditorRequired] public TemplateFieldResponse Field { get; set; } = null!;
    [Parameter] public IList<InlineChildInstance> Instances { get; set; } = [];
    [Parameter] public EventCallback<IList<InlineChildInstance>> InstancesChanged { get; set; }
    [Parameter] public IReadOnlySet<Guid> AncestorTemplateIds { get; set; } = ImmutableHashSet<Guid>.Empty;
    [Parameter] public int Depth { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnMediaPickRequested { get; set; }
    [Parameter] public EventCallback<ContentFieldEditorValue> OnFilePickRequested { get; set; }
    [Parameter] public IReadOnlyDictionary<PrimitiveType, RenderFragment<FieldEditorRenderContext>>? FieldTemplateOverrides { get; set; }

    private readonly Dictionary<InlineChildInstance, ChildRenderState> _stateByInstance = [];
    private bool _candidatesLoaded;
    private IReadOnlyList<TemplateSummaryResponse> _candidates = [];
    private bool _showPicker;

    // A field with a single fixed TemplateId and no AllowedTypes skips the picker entirely - the one
    // candidate is auto-created against directly, with no need to ever resolve a candidate list.
    private Guid? FixedTemplateId => Field.TemplateId.HasValue && Field.AllowedTypes.Count == 0 ? Field.TemplateId : null;

    // The auto-add path (fixed TemplateId, no picker) must not be able to route around the picker's
    // own ancestor-disable check - adding an instance whose template is already an ancestor in this
    // chain would create a genuine A -> B -> A cycle in real content data with no server-side cycle
    // validation to catch it (TemplateGraphValidator exempts IsOpen fields from that check).
    private bool FixedTemplateBlockedByCycle => FixedTemplateId is { } fixedId && AncestorTemplateIds.Contains(fixedId);

    private int ActiveCount => Instances.Count(i => !i.MarkedForDeletion);
    private bool CanAddMore => !Field.MaxOccurrences.HasValue || ActiveCount < Field.MaxOccurrences.Value;
    private bool CanRemove => ActiveCount > Field.MinOccurrences;

    private static string CardClass(InlineChildInstance instance) =>
        instance.MarkedForDeletion ? "cmsify-inline-child-card cmsify-inline-child-card--pending-delete" : "cmsify-inline-child-card";

    protected override async Task OnParametersSetAsync()
    {
        if ((DataSource is null && Client is null) || Depth >= ContentEditSupport.MaxInlineDepth)
        {
            return;
        }

        if (!_candidatesLoaded && !FixedTemplateId.HasValue)
        {
            _candidatesLoaded = true;
            await LoadCandidatesAsync();
        }

        var pending = Instances.Where(i => i.TemplateId.HasValue && !_stateByInstance.ContainsKey(i)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // Reserve a placeholder for each pending instance before awaiting anything, so a re-entrant
        // OnParametersSetAsync (e.g. another instance added while these loads are in flight) doesn't
        // kick off duplicate resolution for the same instance.
        foreach (var instance in pending)
        {
            _stateByInstance[instance] = new ChildRenderState();
        }

        await Task.WhenAll(pending.Select(ResolveChildStateAsync));
    }

    private async Task LoadCandidatesAsync()
    {
        var dataSource = DataSource ?? new CmsifyClientContentEditorDataSource(Client!);
        if (Field.AllowedTypes.Count > 0)
        {
            var ids = Field.AllowedTypes.Where(a => a.AllowedTemplateId.HasValue).Select(a => a.AllowedTemplateId!.Value).Distinct().ToList();
            var lookups = await Task.WhenAll(ids.Select(id => dataSource.GetTemplateAsync(WorkspaceId, id, CancellationToken.None)));
            _candidates = [.. lookups
                .Where(t => t is not null)
                .Select(t => new TemplateSummaryResponse(t!.Id, t.WorkspaceId, t.Name, t.Slug, t.Description, t.CurrentVersion?.Id))];
        }
        else
        {
            _candidates = await dataSource.ListTemplatesAsync(WorkspaceId, CancellationToken.None);
        }
    }

    private async Task ResolveChildStateAsync(InlineChildInstance instance)
    {
        var dataSource = DataSource ?? new CmsifyClientContentEditorDataSource(Client!);
        var state = _stateByInstance[instance];
        var template = await dataSource.GetTemplateAsync(WorkspaceId, instance.TemplateId!.Value, CancellationToken.None);
        var templateVersion = template?.CurrentVersion;
        if (templateVersion is null)
        {
            return;
        }

        state.TemplateVersion = templateVersion;

        await ContentEditSupport.LoadPickListsAsync(dataSource, WorkspaceId, templateVersion.Fields, state.PickListsByRevisionId);
        await ContentEditSupport.LoadReferenceOptionsAsync(dataSource, WorkspaceId, templateVersion.Fields, state.ReferenceOptionsByTemplateId);

        var rootComponentIds = templateVersion.Fields.Where(f => f.ComponentId.HasValue).Select(f => f.ComponentId!.Value);
        var resolvedSchemas = await ComponentSchemaResolver.ResolveAsync(dataSource, WorkspaceId, rootComponentIds);
        foreach (var (id, schema) in resolvedSchemas)
        {
            state.ComponentSchemas[id] = schema;
        }

        // Component schemas must be resolved before this second pick-list/reference-option pass,
        // which extends the same two dictionaries with bindings found nested inside every resolved
        // component schema (the resolver has already flattened the full nested tree).
        var componentFields = state.ComponentSchemas.Values
            .Where(c => c.CurrentVersion is not null)
            .SelectMany(c => c.CurrentVersion!.Fields)
            .Select(ComponentFieldAdapter.ToTemplateField)
            .ToList();
        await ContentEditSupport.LoadPickListsAsync(dataSource, WorkspaceId, componentFields, state.PickListsByRevisionId);
        await ContentEditSupport.LoadReferenceOptionsAsync(dataSource, WorkspaceId, componentFields, state.ReferenceOptionsByTemplateId);
    }

    private Task OnChildFieldChangedAsync(InlineChildInstance instance, (TemplateFieldResponse Field, ContentFieldEditorValue Value) change)
    {
        instance.FieldValues[change.Field.Id] = change.Value;
        return Task.CompletedTask;
    }

    private Task AddInstanceAsync()
    {
        // The fixed-TemplateId path has no picker to fall back to, so a cycle here is caught by
        // FixedTemplateBlockedByCycle hiding the Add button entirely (see markup above) rather than
        // by this guard - but re-check here too, defensively, in case Add is ever reachable some
        // other way while a cycle is present.
        if (FixedTemplateId is { } fixedId)
        {
            return AncestorTemplateIds.Contains(fixedId) ? Task.CompletedTask : CommitNewInstanceAsync(fixedId);
        }

        // A single open/AllowedTypes candidate that's already an ancestor in this chain must not be
        // auto-created either - that would silently create a cycle, bypassing the picker's own
        // ancestor-disable check entirely. Fall through to the picker instead, where the one
        // candidate renders visibly disabled.
        if (_candidates.Count == 1 && !AncestorTemplateIds.Contains(_candidates[0].Id))
        {
            return CommitNewInstanceAsync(_candidates[0].Id);
        }

        _showPicker = true;
        return Task.CompletedTask;
    }

    private Task OnNewInstanceTemplateSelectedAsync(Guid templateId) => CommitNewInstanceAsync(templateId);

    private Task CommitNewInstanceAsync(Guid templateId)
    {
        _showPicker = false;
        var updated = Instances.ToList();
        updated.Add(new InlineChildInstance { TemplateId = templateId });
        return InstancesChanged.InvokeAsync(updated);
    }

    private Task RemoveInstanceAsync(InlineChildInstance instance)
    {
        if (instance.ContentItemId is null)
        {
            return InstancesChanged.InvokeAsync([.. Instances.Where(i => i != instance)]);
        }

        instance.MarkedForDeletion = true;
        return InstancesChanged.InvokeAsync(Instances);
    }

    private Task RestoreInstanceAsync(InlineChildInstance instance)
    {
        instance.MarkedForDeletion = false;
        return InstancesChanged.InvokeAsync(Instances);
    }

    private sealed class ChildRenderState
    {
        public TemplateVersionResponse? TemplateVersion { get; set; }
        public Dictionary<Guid, PickListResponse> PickListsByRevisionId { get; } = [];
        public Dictionary<Guid, IReadOnlyList<ContentItemSummaryResponse>> ReferenceOptionsByTemplateId { get; } = [];
        public Dictionary<Guid, ComponentResponse> ComponentSchemas { get; } = [];
    }
}
