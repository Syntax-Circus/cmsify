using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

/// <summary>Workspace-scoped, inline-only reusable content schema.</summary>
public sealed class ComponentDefinition : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }
    public string? PackageNamespace { get; set; }
    public string? PackageId { get; set; }
    public string? PackageVersion { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public IList<ComponentVersion> Versions { get; } = new List<ComponentVersion>();
}

public sealed class ComponentVersion : SoftDeletableEntity
{
    public Guid ComponentId { get; set; }
    public int VersionNumber { get; set; }
    public TemplateVersionStatus Status { get; set; } = TemplateVersionStatus.Draft;
    public DateTimeOffset? PublishedAt { get; set; }
    public string? Notes { get; set; }
    public IList<ComponentField> Fields { get; } = new List<ComponentField>();
}

public sealed class ComponentField : Entity
{
    public Guid ComponentVersionId { get; set; }
    public required string Key { get; set; }
    public required string Label { get; set; }
    public string? HelpText { get; set; }
    public int Order { get; set; }
    public bool IsRequired { get; set; }
    public int MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public PrimitiveType? PrimitiveType { get; set; }
    public Guid? NestedComponentId { get; set; }
    public JsonElement? FieldConfig { get; set; }
}
