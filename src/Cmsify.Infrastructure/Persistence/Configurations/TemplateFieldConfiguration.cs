using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TemplateFieldConfiguration : IEntityTypeConfiguration<TemplateField>
{
    public void Configure(EntityTypeBuilder<TemplateField> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(field => new { field.TemplateVersionId, field.Key }).IsUnique();

        builder.HasOne<TemplateVersion>()
            .WithMany(version => version.Fields)
            .HasForeignKey(field => field.TemplateVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateSection>()
            .WithMany()
            .HasForeignKey(field => field.SectionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        builder.HasOne<Template>()
            .WithMany()
            .HasForeignKey(field => field.TemplateId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.Ignore(field => field.ReferencedTemplateVersion);

        builder.Property(field => field.Key).HasMaxLength(100).IsRequired();
        builder.Property(field => field.Label).HasMaxLength(200).IsRequired();
        builder.Property(field => field.HelpText).HasMaxLength(1_000);
        builder.Property(field => field.CompositionMode).HasConversion<string>().HasMaxLength(50);
        builder.Property(field => field.PrimitiveType).HasConversion<string>().HasMaxLength(50);
        builder.Property(field => field.FieldConfig).HasColumnType("jsonb");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_template_fields_type_shape",
            "(is_open = true AND primitive_type IS NULL AND template_id IS NULL) OR (is_open = false AND ((primitive_type IS NOT NULL AND template_id IS NULL) OR (primitive_type IS NULL AND template_id IS NOT NULL)))"));
    }
}
