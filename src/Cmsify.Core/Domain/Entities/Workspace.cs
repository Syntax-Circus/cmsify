namespace Cmsify.Core.Domain.Entities;

public sealed class Workspace : SoftDeletableEntity
{
    public required string Name { get; set; }

    public required string Slug { get; set; }

    public string? Description { get; set; }
}
