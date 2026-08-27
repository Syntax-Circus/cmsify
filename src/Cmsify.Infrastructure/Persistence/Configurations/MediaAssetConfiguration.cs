using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(asset => asset.WorkspaceId);
        builder.HasIndex(asset => new { asset.BlobState, asset.BlobStateChangedAt });

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(asset => asset.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(asset => asset.FileName).HasMaxLength(255).IsRequired();
        builder.Property(asset => asset.MimeType).HasMaxLength(255).IsRequired();
        builder.Property(asset => asset.StorageKey).HasMaxLength(1_000).IsRequired();
        builder.Property(asset => asset.StorageProvider).HasMaxLength(50).IsRequired();
        builder.Property(asset => asset.AltText).HasMaxLength(500);
        builder.Property(asset => asset.BlobState).HasConversion<string>().HasMaxLength(50).IsRequired();
    }
}
