using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(template => new { template.WorkspaceId, template.Slug })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(template => template.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany()
            .HasForeignKey(template => template.CurrentVersionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(template => template.Name).HasMaxLength(200).IsRequired();
        builder.Property(template => template.Slug).HasMaxLength(100).IsRequired();
        builder.Property(template => template.Description).HasMaxLength(1_000);
        builder.Property(template => template.PackageNamespace).HasMaxLength(200);
        builder.Property(template => template.PackageId).HasMaxLength(200);
        builder.Property(template => template.PackageVersion).HasMaxLength(50);
        builder.Property(template => template.TitleFieldKey).HasMaxLength(100);
    }
}
