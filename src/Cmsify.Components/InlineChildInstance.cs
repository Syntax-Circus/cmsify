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
}
