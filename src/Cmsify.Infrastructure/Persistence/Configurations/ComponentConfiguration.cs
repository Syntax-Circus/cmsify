using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ComponentDefinitionConfiguration : IEntityTypeConfiguration<ComponentDefinition>
{
    public void Configure(EntityTypeBuilder<ComponentDefinition> builder)
    {
        builder.ConfigureEntityId(); builder.ConfigureSoftDelete(); builder.ConfigureXminConcurrency();
        builder.Property(component => component.Name).HasMaxLength(200).IsRequired();
        builder.Property(component => component.Slug).HasMaxLength(200).IsRequired();
        builder.Property(component => component.Description).HasMaxLength(1_000);
        builder.Property(component => component.PackageNamespace).HasMaxLength(200);
        builder.Property(component => component.PackageId).HasMaxLength(200);
        builder.Property(component => component.PackageVersion).HasMaxLength(50);
        builder.HasIndex(component => new { component.WorkspaceId, component.Slug }).IsUnique().HasFilter("is_deleted = false");
        builder.HasOne<Workspace>().WithMany().HasForeignKey(component => component.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ComponentVersion>().WithMany().HasForeignKey(component => component.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ComponentVersionConfiguration : IEntityTypeConfiguration<ComponentVersion>
{
    public void Configure(EntityTypeBuilder<ComponentVersion> builder)
    {
        builder.ConfigureEntityId(); builder.ConfigureSoftDelete(); builder.ConfigureXminConcurrency();
        builder.HasIndex(version => new { version.ComponentId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => version.ComponentId).IsUnique().HasFilter("status = 'Draft' AND is_deleted = false");
        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(version => version.Notes).HasMaxLength(2_000);
        builder.HasOne<ComponentDefinition>().WithMany(component => component.Versions).HasForeignKey(version => version.ComponentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ComponentFieldConfiguration : IEntityTypeConfiguration<ComponentField>
{
    public void Configure(EntityTypeBuilder<ComponentField> builder)
    {
        builder.ConfigureEntityId();
        builder.Property(field => field.Key).HasMaxLength(100).IsRequired(); builder.Property(field => field.Label).HasMaxLength(200).IsRequired(); builder.Property(field => field.HelpText).HasMaxLength(1_000);
        builder.Property(field => field.PrimitiveType).HasConversion<string>().HasMaxLength(50); builder.Property(field => field.FieldConfig).HasColumnType("jsonb");
        builder.HasIndex(field => new { field.ComponentVersionId, field.Key }).IsUnique();
        builder.HasOne<ComponentVersion>().WithMany(version => version.Fields).HasForeignKey(field => field.ComponentVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ComponentDefinition>().WithMany().HasForeignKey(field => field.NestedComponentId).OnDelete(DeleteBehavior.Restrict);
    }
}
