using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentItemConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(content => new { content.WorkspaceId, content.TemplateVersionId, content.Slug })
            .IsUnique()
            .HasFilter("slug IS NOT NULL AND is_deleted = false");
        builder.HasIndex(content => content.TranslationGroupId);
        builder.HasIndex(content => new { content.Status, content.PublishAt });
        builder.HasIndex(content => new { content.Status, content.PublishAt, content.PendingEffectiveStartAt, content.PendingEffectiveEndAt });
        builder.HasIndex(content => content.WorkspaceId);
        builder.HasIndex(content => content.SearchVector).HasMethod("GIN");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(content => content.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany()
            .HasForeignKey(content => content.TemplateVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(content => content.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(content => content.Slug).HasMaxLength(200);
        builder.Property(content => content.LocaleCode).HasMaxLength(20);
#pragma warning disable CS0618
        builder.Property(content => content.SearchVector)
            .HasColumnType("tsvector")
            .HasConversion(
                value => NpgsqlTsVector.Parse(value ?? string.Empty),
                value => value.ToString());
#pragma warning restore CS0618
    }
}
