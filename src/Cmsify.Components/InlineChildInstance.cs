namespace SyntaxCircus.Cmsify.Components;

public sealed class InlineChildInstance
{
    public Guid? ContentItemId { get; set; }
    public Guid? TemplateId { get; set; }              // null = not yet chosen; fixed forever once set (no API moves a ContentItem to a different template)
    public int? VersionNumber { get; set; }

    // The status of the version resolved for VersionNumber at load time. Draft/Review/Approved is
    // already directly editable; anything else (typically Published or Archived, when the child has
    // no Draft yet) means SaveInlineFieldAsync must mint a fresh Draft - duplicated from this
    // version - before it can save this instance's edits (see ContentEditSupport's lazy-draft
    // comments). Null for a not-yet-persisted instance (ContentItemId is null).
    public ContentStatus? VersionStatus { get; set; }

    // Threaded from LoadInlineChildInstanceAsync's own allowDraftCreation parameter (true by default
    // so a freshly-created "add new" instance, which never goes through that loader, isn't
    // accidentally blocked from ever getting its first Draft). False only for a read-only view or an
    // explicit historical VersionNumber - mirrors the load-time gate this replaces.
    public bool AllowDraftCreation { get; set; } = true;
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
