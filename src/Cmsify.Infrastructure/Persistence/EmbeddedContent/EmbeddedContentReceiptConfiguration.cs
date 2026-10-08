using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

public sealed class EmbeddedContentReceiptConfiguration : IEntityTypeConfiguration<EmbeddedContentReceipt>
{
    public void Configure(EntityTypeBuilder<EmbeddedContentReceipt> builder)
    {
        builder.ToTable("embedded_content_receipts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ContractFingerprint).HasColumnType("char(64)");
        builder.Property(x => x.InputFingerprint).HasColumnType("char(64)");
        builder.HasIndex(x => new { x.WorkspaceId, x.Kind, x.OperationKey }).IsUnique();
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ContentItem>().WithMany().HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ContentVersion>().WithMany().HasForeignKey(x => x.ContentVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
