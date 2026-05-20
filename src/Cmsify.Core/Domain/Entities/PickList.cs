namespace Cmsify.Core.Domain.Entities;

public sealed class PickList : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public required string Slug { get; set; }

    public string? Description { get; set; }

    public IList<PickListOption> Options { get; } = new List<PickListOption>();
}

public sealed class PickListOption : Entity
{
    public Guid PickListId { get; set; }

    public required string Label { get; set; }

    public required string Value { get; set; }

    public int Order { get; set; }
}
