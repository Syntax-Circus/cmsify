using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Tests;

public sealed class TemplateBuilder
{
    private readonly Template template = new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        Name = "Article",
        Slug = "article"
    };

    public TemplateBuilder WithId(Guid id)
    {
        template.Id = id;
        return this;
    }

    public TemplateBuilder InWorkspace(Guid workspaceId)
    {
        template.WorkspaceId = workspaceId;
        return this;
    }

    public TemplateBuilder WithName(string name, string? slug = null)
    {
        template.Name = name;
        template.Slug = slug ?? name.ToLowerInvariant().Replace(' ', '-');
        return this;
    }

    public TemplateBuilder WithCurrentVersion(Guid versionId)
    {
        template.CurrentVersionId = versionId;
        return this;
    }

    public Template Build() => template;
}

public sealed class TemplateVersionBuilder
{
    private readonly TemplateVersion version = new()
    {
        TemplateId = Guid.CreateVersion7(),
        VersionNumber = 1,
        Status = TemplateVersionStatus.Draft
    };

    public TemplateVersionBuilder WithId(Guid id)
    {
        version.Id = id;
        return this;
    }

    public TemplateVersionBuilder ForTemplate(Guid templateId)
    {
        version.TemplateId = templateId;
        return this;
    }

    public TemplateVersionBuilder WithStatus(TemplateVersionStatus status)
    {
        version.Status = status;
        version.PublishedAt = status == TemplateVersionStatus.Published ? DateTimeOffset.UtcNow : null;
        return this;
    }

    public TemplateVersionBuilder WithField(string key, PrimitiveType primitiveType = PrimitiveType.Text, bool isRequired = false)
    {
        version.Fields.Add(new TemplateField
        {
            TemplateVersionId = version.Id,
            Key = key,
            Label = key,
            PrimitiveType = primitiveType,
            CompositionMode = CompositionMode.Inline,
            IsRequired = isRequired,
            MinOccurrences = isRequired ? 1 : 0,
            Order = version.Fields.Count
        });
        return this;
    }

    public TemplateVersion Build() => version;
}

public sealed class ContentItemBuilder
{
    private readonly ContentItem item = new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        TemplateVersionId = Guid.CreateVersion7(),
        Status = ContentStatus.Draft
    };

    public ContentItemBuilder WithId(Guid id)
    {
        item.Id = id;
        return this;
    }

    public ContentItemBuilder InWorkspace(Guid workspaceId)
    {
        item.WorkspaceId = workspaceId;
        return this;
    }

    public ContentItemBuilder ForTemplateVersion(Guid templateVersionId)
    {
        item.TemplateVersionId = templateVersionId;
        return this;
    }

    public ContentItemBuilder WithStatus(ContentStatus status)
    {
        item.Status = status;
        item.PublishedAt = status == ContentStatus.Published ? DateTimeOffset.UtcNow : null;
        item.ArchivedAt = status == ContentStatus.Archived ? DateTimeOffset.UtcNow : null;
        return this;
    }

    public ContentItemBuilder WithTextValue(Guid fieldId, string value, int order = 0)
    {
        item.FieldValues.Add(new ContentFieldValue
        {
            ContentItemId = item.Id,
            FieldId = fieldId,
            ValueKind = ValueKind.Text,
            TextValue = value,
            Order = order
        });
        return this;
    }

    public ContentItem Build() => item;
}
