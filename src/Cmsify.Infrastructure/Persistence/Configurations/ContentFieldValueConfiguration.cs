using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentFieldValueConfiguration : IEntityTypeConfiguration<ContentFieldValue>
{
    public void Configure(EntityTypeBuilder<ContentFieldValue> builder)
    {
        builder.ConfigureEntityId();

        builder.HasOne<ContentItem>()
            .WithMany(content => content.FieldValues)
            .HasForeignKey(value => value.ContentItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateField>()
            .WithMany()
            .HasForeignKey(value => value.FieldId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(value => value.MediaAssetId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(value => value.FileAssetId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasOne<ContentItem>()
            .WithMany()
            .HasForeignKey(value => value.ChildContentItemId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasIndex(value => new { value.ContentItemId, value.FieldId, value.Order });
        builder.Property(value => value.ValueKind).HasConversion<string>().HasMaxLength(50);
        builder.Property(value => value.TextValue);
        builder.Property(value => value.JsonValue).HasColumnType("jsonb");
    }
}
