using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class MediaDeletionIntentConfiguration : IEntityTypeConfiguration<MediaDeletionIntent>
{
    public void Configure(EntityTypeBuilder<MediaDeletionIntent> builder)
    {
        builder.ConfigureEntityId();
        builder.HasIndex(intent => new { intent.Provider, intent.StorageKey })
            .IsUnique()
            .HasFilter("completed_at IS NULL");
        builder.HasIndex(intent => new { intent.CompletedAt, intent.NextAttemptAt, intent.LeaseExpiresAt });
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(intent => intent.MediaAssetId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Property(intent => intent.Provider).HasMaxLength(50).IsRequired();
        builder.Property(intent => intent.StorageKey).HasMaxLength(1_000).IsRequired();
        builder.Property(intent => intent.Reason).HasMaxLength(100).IsRequired();
        builder.Property(intent => intent.LastError).HasMaxLength(2_000);
        builder.Property(intent => intent.LeaseOwner).HasMaxLength(200);
    }
}

public sealed class MediaReconciliationCheckpointConfiguration : IEntityTypeConfiguration<MediaReconciliationCheckpoint>
{
    public void Configure(EntityTypeBuilder<MediaReconciliationCheckpoint> builder)
    {
        builder.ConfigureEntityId();
        builder.HasIndex(checkpoint => new { checkpoint.Provider, checkpoint.Prefix }).IsUnique();
        builder.Property(checkpoint => checkpoint.Provider).HasMaxLength(50).IsRequired();
        builder.Property(checkpoint => checkpoint.Prefix).HasMaxLength(1_000).IsRequired();
        builder.Property(checkpoint => checkpoint.AfterKey).HasMaxLength(1_000);
        builder.Property(checkpoint => checkpoint.LeaseOwner).HasMaxLength(200);
    }
}
