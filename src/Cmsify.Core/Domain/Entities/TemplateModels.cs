using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class Template : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public required string Slug { get; set; }

    public string? Description { get; set; }

    public string? PackageNamespace { get; set; }

    public string? PackageId { get; set; }

    public string? PackageVersion { get; set; }

    public string? TitleFieldKey { get; set; }

    public Guid? CurrentVersionId { get; set; }

    public IList<TemplateVersion> Versions { get; } = new List<TemplateVersion>();
}

public sealed class TemplateVersion : SoftDeletableEntity
{
    public Guid TemplateId { get; set; }

    public int VersionNumber { get; set; }

    public TemplateVersionStatus Status { get; set; } = TemplateVersionStatus.Draft;

    public DateTimeOffset? PublishedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public string? Notes { get; set; }

    public IList<TemplateSection> Sections { get; } = new List<TemplateSection>();

    public IList<TemplateField> Fields { get; } = new List<TemplateField>();
}

public sealed class TemplateSection : Entity
{
    public Guid TemplateVersionId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int Order { get; set; }

    public bool IsCollapsible { get; set; }
}

public sealed class TemplateField : Entity
{
    public Guid TemplateVersionId { get; set; }

    public Guid? SectionId { get; set; }

    public required string Key { get; set; }

    public required string Label { get; set; }

    public string? HelpText { get; set; }

    public int Order { get; set; }

    public bool IsRequired { get; set; }

    public int MinOccurrences { get; set; }

    public int? MaxOccurrences { get; set; }

    public bool IsOpen { get; set; }

    public CompositionMode CompositionMode { get; set; }

    public PrimitiveType? PrimitiveType { get; set; }

    public Guid? TemplateId { get; set; }

    public Guid? ComponentId { get; set; }

    public JsonElement? FieldConfig { get; set; }

    public TemplateVersion? ReferencedTemplateVersion { get; set; }

    public IList<TemplateFieldAllowedType> AllowedTypes { get; } = new List<TemplateFieldAllowedType>();
}

public sealed class TemplateFieldAllowedType : Entity
{
    public Guid FieldId { get; set; }

    public PrimitiveType? PrimitiveType { get; set; }

    public Guid? AllowedTemplateId { get; set; }
}
