using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(tag => new { tag.WorkspaceId, tag.Name })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(tag => tag.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(tag => tag.Name).HasMaxLength(100).IsRequired();
    }
}
