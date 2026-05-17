using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(version => new { version.TemplateId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => version.TemplateId)
            .IsUnique()
            .HasFilter("status = 'Draft' AND is_deleted = false")
            .HasDatabaseName("ix_template_versions_one_draft_per_template");

        builder.HasOne<Template>()
            .WithMany(template => template.Versions)
            .HasForeignKey(version => version.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(version => version.Notes).HasMaxLength(2_000);
    }
}
