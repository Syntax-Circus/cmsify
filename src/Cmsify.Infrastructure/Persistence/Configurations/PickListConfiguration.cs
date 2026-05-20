using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class PickListConfiguration : IEntityTypeConfiguration<PickList>
{
    public void Configure(EntityTypeBuilder<PickList> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(picklist => new { picklist.WorkspaceId, picklist.Slug })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(picklist => picklist.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(picklist => picklist.Name).HasMaxLength(200).IsRequired();
        builder.Property(picklist => picklist.Slug).HasMaxLength(100).IsRequired();
        builder.Property(picklist => picklist.Description).HasMaxLength(1_000);
    }
}

public sealed class PickListOptionConfiguration : IEntityTypeConfiguration<PickListOption>
{
    public void Configure(EntityTypeBuilder<PickListOption> builder)
    {
        builder.ConfigureEntityId();

        builder.HasOne<PickList>()
            .WithMany(picklist => picklist.Options)
            .HasForeignKey(option => option.PickListId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(option => new { option.PickListId, option.Value }).IsUnique();
        builder.HasIndex(option => new { option.PickListId, option.Order });

        builder.Property(option => option.Label).HasMaxLength(200).IsRequired();
        builder.Property(option => option.Value).HasMaxLength(200).IsRequired();
    }
}
