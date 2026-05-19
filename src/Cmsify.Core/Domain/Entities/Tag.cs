namespace Cmsify.Core.Domain.Entities;

public sealed class Tag : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }
}
