using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => session.UserId);
        builder.HasIndex(session => session.ExpiresAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(session => session.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(session => session.IpAddress).HasMaxLength(128);
    }
}
