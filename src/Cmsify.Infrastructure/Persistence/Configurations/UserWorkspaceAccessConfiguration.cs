using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class UserWorkspaceAccessConfiguration : IEntityTypeConfiguration<UserWorkspaceAccess>
{
    public void Configure(EntityTypeBuilder<UserWorkspaceAccess> builder)
    {
        builder.ConfigureEntityId();

        builder.Property(access => access.AccessLevel).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(access => new { access.UserId, access.WorkspaceId })
            .IsUnique();

        builder.HasOne(access => access.Workspace)
            .WithMany()
            .HasForeignKey(access => access.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
