using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedTemplateConfiguration : IEntityTypeConfiguration<EmbeddedTemplateRegistration>
{
    public void Configure(EntityTypeBuilder<EmbeddedTemplateRegistration> builder)
    {
        builder.ToTable("embedded_template_registrations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ContractKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Fingerprint).HasColumnType("char(64)").IsRequired();
        builder.HasIndex(x => new { x.WorkspaceId, x.ContractKey }).IsUnique();
        builder.HasIndex(x => x.TemplateVersionId).IsUnique();
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Template>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
