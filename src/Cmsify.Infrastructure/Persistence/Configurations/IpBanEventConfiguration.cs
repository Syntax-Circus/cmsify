using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class IpBanEventConfiguration : IEntityTypeConfiguration<IpBanEvent>
{
    public void Configure(EntityTypeBuilder<IpBanEvent> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(banEvent => banEvent.IpAddress);
        builder.HasIndex(banEvent => banEvent.BannedAt);

        builder.Property(banEvent => banEvent.IpAddress).HasMaxLength(45).IsRequired();
        builder.Property(banEvent => banEvent.RequestPath).HasMaxLength(2048);
        builder.Property(banEvent => banEvent.BannedAt).IsRequired();
        builder.Property(banEvent => banEvent.BannedUntil).IsRequired();
    }
}
