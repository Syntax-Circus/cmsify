using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(audit => new { audit.EntityType, audit.EntityId });
        builder.HasIndex(audit => audit.WorkspaceId);
        builder.HasIndex(audit => audit.Timestamp);

        builder.Property(audit => audit.EntityType).HasMaxLength(200).IsRequired();
        builder.Property(audit => audit.Action).HasConversion<string>().HasMaxLength(50);
        builder.Property(audit => audit.ChangeDelta).HasColumnType("jsonb");
        builder.Property(audit => audit.Timestamp).IsRequired();
    }
}
