using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class WebhookDeliveryLogConfiguration : IEntityTypeConfiguration<WebhookDeliveryLog>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryLog> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(log => new { log.IsDelivered, log.IsFailed, log.NextRetryAt });
        builder.HasIndex(log => log.WebhookEndpointId);

        builder.HasOne<WebhookEndpoint>()
            .WithMany()
            .HasForeignKey(log => log.WebhookEndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(log => log.EventType).HasMaxLength(200).IsRequired();
        builder.Property(log => log.Payload).HasColumnType("jsonb");
        builder.Property(log => log.CreatedAt).IsRequired();
    }
}
