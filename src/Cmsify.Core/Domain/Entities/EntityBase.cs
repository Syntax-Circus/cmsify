namespace Cmsify.Core.Domain.Entities;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

public abstract class TimestampedEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public abstract class SoftDeletableEntity : TimestampedEntity
{
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedByUserId { get; set; }
}
