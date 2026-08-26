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

    public DateTimeOffset? PendingEffectiveStartAt { get; set; }

    public DateTimeOffset? PendingEffectiveEndAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? PublishLeaseOwner { get; set; }

    public Guid? PublishLeaseToken { get; set; }

    public DateTimeOffset? PublishLeaseExpiresAt { get; set; }

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

public sealed class ContentVersion : Entity
{
    public Guid ContentItemId { get; set; }

    public Guid WorkspaceId { get; set; }

    public int VersionNumber { get; set; }

    public ContentVersionStatus Status { get; set; } = ContentVersionStatus.Published;

    public Guid TemplateVersionId { get; set; }

    public string? Slug { get; set; }

    public string? LocaleCode { get; set; }

    public Guid? TranslationGroupId { get; set; }

    public IList<string> Tags { get; set; } = new List<string>();

    public DateTimeOffset? EffectiveStartAt { get; set; }

    public DateTimeOffset? EffectiveEndAt { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RetiredAt { get; set; }

    public Guid? PublishedByUserId { get; set; }

    public int? RolledBackFromVersionNumber { get; set; }

    public IList<ContentVersionFieldValue> FieldValues { get; } = new List<ContentVersionFieldValue>();
}

public sealed class ContentVersionFieldValue : Entity
{
    public Guid ContentVersionId { get; set; }

    public Guid FieldId { get; set; }

    public int Order { get; set; }

    public ValueKind ValueKind { get; set; }

    public string? TextValue { get; set; }

    /// <summary>Label selected at publication time for a pick-list value.</summary>
    public string? DisplayLabel { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? MediaAssetId { get; set; }

    public Guid? FileAssetId { get; set; }

    public Guid? ChildContentItemId { get; set; }

    public JsonElement? JsonValue { get; set; }
}
