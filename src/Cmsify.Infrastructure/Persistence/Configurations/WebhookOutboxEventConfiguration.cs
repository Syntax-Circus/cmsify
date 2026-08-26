using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class WebhookOutboxEventConfiguration : IEntityTypeConfiguration<WebhookOutboxEvent>
{
    public void Configure(EntityTypeBuilder<WebhookOutboxEvent> builder)
    {
        builder.ConfigureEntityId();
        builder.Property(evt => evt.EventType).HasMaxLength(200).IsRequired();
        builder.Property(evt => evt.Payload).HasColumnType("jsonb");
        builder.Property(evt => evt.OccurredAt).IsRequired();
        builder.Property(evt => evt.CreatedAt).IsRequired();
        builder.Property(evt => evt.LeaseOwner).HasMaxLength(200);
        builder.HasIndex(evt => new { evt.EventType, evt.WorkspaceId, evt.OccurredAt });
        builder.HasIndex(evt => new { evt.ProcessedAt, evt.LeaseExpiresAt });
    }
}
