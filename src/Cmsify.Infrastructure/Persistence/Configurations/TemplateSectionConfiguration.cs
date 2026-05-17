using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TemplateSectionConfiguration : IEntityTypeConfiguration<TemplateSection>
{
    public void Configure(EntityTypeBuilder<TemplateSection> builder)
    {
        builder.ConfigureEntityId();

        builder.HasOne<TemplateVersion>()
            .WithMany(version => version.Sections)
            .HasForeignKey(section => section.TemplateVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(section => new { section.TemplateVersionId, section.Order });
        builder.Property(section => section.Name).HasMaxLength(200).IsRequired();
        builder.Property(section => section.Description).HasMaxLength(1_000);
    }
}
