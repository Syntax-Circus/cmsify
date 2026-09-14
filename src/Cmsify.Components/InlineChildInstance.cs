namespace SyntaxCircus.Cmsify.Components;

public sealed class InlineChildInstance
{
    public Guid? ContentItemId { get; set; }
    public Guid? TemplateId { get; set; }              // null = not yet chosen; fixed forever once set (no API moves a ContentItem to a different template)
    public int? VersionNumber { get; set; }
    public string? Slug { get; set; }
    public string? Locale { get; set; }
    public string? Tags { get; set; }
    public Guid? TranslationGroupId { get; set; }
    public Dictionary<Guid, ContentFieldEditorValue> FieldValues { get; set; } = [];
    public DateTimeOffset? EffectiveStartAt { get; set; }
    public DateTimeOffset? EffectiveEndAt { get; set; }
    public bool MarkedForDeletion { get; set; }

    // Set when this instance's own content failed to load client-side (deleted out-of-band,
    // transient error, permission issue). FieldValues is empty/unreliable in this state - the
    // instance exists purely to preserve ContentItemId so a subsequent save doesn't silently drop
    // the ChildContent link (and orphan every healthy sibling of the same field in the process).
    public bool LoadFailed { get; set; }
}
