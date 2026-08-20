using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(client => client.WorkspaceId);
        builder.HasIndex(client => client.TokenIdentifier).IsUnique().HasFilter("token_identifier IS NOT NULL");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(client => client.WorkspaceId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(client => client.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(client => client.Name).HasMaxLength(200).IsRequired();
        builder.Property(client => client.Description).HasMaxLength(1_000);
        builder.Property(client => client.TokenHash).HasMaxLength(500).IsRequired();
        builder.Property(client => client.TokenIdentifier).HasMaxLength(64);
        builder.Property(client => client.Role).HasConversion<string>().HasMaxLength(50);
    }
}
