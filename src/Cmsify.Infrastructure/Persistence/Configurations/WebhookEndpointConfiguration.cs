using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(endpoint => new { endpoint.WorkspaceId, endpoint.Name })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(endpoint => endpoint.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(endpoint => endpoint.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(endpoint => endpoint.Name).HasMaxLength(200).IsRequired();
        builder.Property(endpoint => endpoint.Url).HasMaxLength(2_000).IsRequired();
        builder.Property(endpoint => endpoint.Secret).HasMaxLength(1_000).IsRequired();
    }
}
