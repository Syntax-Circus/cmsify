namespace Cmsify.Core.Domain.Entities;

public sealed class PickList : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public required string Slug { get; set; }

    public string? Description { get; set; }

    public Guid? CurrentRevisionId { get; set; }

    public IList<PickListOption> Options { get; } = new List<PickListOption>();
}

public sealed class PickListRevision : Entity
{
    public Guid PickListId { get; set; }
    public int VersionNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public IList<PickListRevisionOption> Options { get; } = new List<PickListRevisionOption>();
}

public sealed class PickListRevisionOption : Entity
{
    public Guid PickListRevisionId { get; set; }
    public required string Label { get; set; }
    public required string Value { get; set; }
    public int Order { get; set; }
}

public sealed class PickListOption : Entity
{
    public Guid PickListId { get; set; }

    public required string Label { get; set; }

    public required string Value { get; set; }

    public int Order { get; set; }
}
