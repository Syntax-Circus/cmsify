using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(user => user.Email)
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.Property(user => user.Email).HasMaxLength(320).IsRequired();
        builder.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(user => user.Role).HasConversion<string>().HasMaxLength(50);
        builder.Property(user => user.IsSuperAdmin).HasDefaultValue(false);
        builder.Property(user => user.TimeZoneId).HasMaxLength(100);
        builder.Property(user => user.Theme).HasMaxLength(20);
        builder.HasMany(user => user.WorkspaceAccesses)
            .WithOne(access => access.User)
            .HasForeignKey(access => access.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
