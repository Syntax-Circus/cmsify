using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentVersionConfiguration : IEntityTypeConfiguration<ContentVersion>
{
    public void Configure(EntityTypeBuilder<ContentVersion> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(version => new { version.ContentItemId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => new { version.ContentItemId, version.Status })
            .IsUnique()
            .HasFilter("status = 'Published'");
        builder.HasIndex(version => version.WorkspaceId);

        builder.HasOne<ContentItem>()
            .WithMany()
            .HasForeignKey(version => version.ContentItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany()
            .HasForeignKey(version => version.TemplateVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(version => version.Slug).HasMaxLength(200);
        builder.Property(version => version.LocaleCode).HasMaxLength(20);
        builder.Property(version => version.Tags)
            .HasColumnType("text[]")
            .HasConversion(
                tags => tags.ToArray(),
                array => array.ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IList<string>>(
                    (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                    list => list == null ? 0 : list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    list => (IList<string>)list.ToList()));
    }
}

public sealed class ContentVersionFieldValueConfiguration : IEntityTypeConfiguration<ContentVersionFieldValue>
{
    public void Configure(EntityTypeBuilder<ContentVersionFieldValue> builder)
    {
        builder.ConfigureEntityId();

        builder.HasOne<ContentVersion>()
            .WithMany(version => version.FieldValues)
            .HasForeignKey(value => value.ContentVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(value => new { value.ContentVersionId, value.FieldId, value.Order });
        builder.Property(value => value.ValueKind).HasConversion<string>().HasMaxLength(50);
        builder.Property(value => value.TextValue);
        builder.Property(value => value.JsonValue).HasColumnType("jsonb");
    }
}
