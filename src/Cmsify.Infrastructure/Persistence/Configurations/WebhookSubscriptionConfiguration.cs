using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        builder.HasKey(subscription => new { subscription.WebhookEndpointId, subscription.EventType });

        builder.HasOne<WebhookEndpoint>()
            .WithMany(endpoint => endpoint.Subscriptions)
            .HasForeignKey(subscription => subscription.WebhookEndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(subscription => subscription.EventType).HasMaxLength(200).IsRequired();
    }
}
