using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class TemplateFieldAllowedTypeConfiguration : IEntityTypeConfiguration<TemplateFieldAllowedType>
{
    public void Configure(EntityTypeBuilder<TemplateFieldAllowedType> builder)
    {
        builder.ConfigureEntityId();

        builder.HasIndex(type => new { type.FieldId, type.PrimitiveType })
            .IsUnique()
            .HasFilter("primitive_type IS NOT NULL");
        builder.HasIndex(type => new { type.FieldId, type.AllowedTemplateId })
            .IsUnique()
            .HasFilter("allowed_template_id IS NOT NULL");

        builder.HasOne<TemplateField>()
            .WithMany(field => field.AllowedTypes)
            .HasForeignKey(type => type.FieldId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Template>()
            .WithMany()
            .HasForeignKey(type => type.AllowedTemplateId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.Property(type => type.PrimitiveType).HasConversion<string>().HasMaxLength(50);

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_template_field_allowed_types_type_shape",
            "(primitive_type IS NOT NULL AND allowed_template_id IS NULL) OR (primitive_type IS NULL AND allowed_template_id IS NOT NULL)"));
    }
}
