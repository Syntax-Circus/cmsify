using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ConfigureEntityId();
        builder.ConfigureSoftDelete();
        builder.ConfigureXminConcurrency();

        builder.HasIndex(workspace => workspace.Slug).IsUnique();
        builder.Property(workspace => workspace.Name).HasMaxLength(200).IsRequired();
        builder.Property(workspace => workspace.Slug).HasMaxLength(100).IsRequired();
        builder.Property(workspace => workspace.Description).HasMaxLength(1_000);
    }
}
