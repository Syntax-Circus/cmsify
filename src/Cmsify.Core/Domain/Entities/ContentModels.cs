using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class ContentItem : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid TemplateVersionId { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    public string? Slug { get; set; }

    public string? LocaleCode { get; set; }

    public Guid? TranslationGroupId { get; set; }

    public DateTimeOffset? PublishAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? SearchVector { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public IList<ContentFieldValue> FieldValues { get; } = new List<ContentFieldValue>();

    public IList<ContentItemTag> Tags { get; } = new List<ContentItemTag>();
}

public sealed class ContentFieldValue : Entity
{
    public Guid ContentItemId { get; set; }

    public Guid FieldId { get; set; }

    public int Order { get; set; }

    public ValueKind ValueKind { get; set; }

    public string? TextValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? MediaAssetId { get; set; }

    public Guid? FileAssetId { get; set; }

    public Guid? ChildContentItemId { get; set; }

    public JsonElement? JsonValue { get; set; }
}

public sealed class ContentItemTag
{
    public Guid ContentItemId { get; set; }

    public Guid TagId { get; set; }
}
