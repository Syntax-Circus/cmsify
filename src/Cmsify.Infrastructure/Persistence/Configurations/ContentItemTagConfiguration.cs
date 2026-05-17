using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cmsify.Infrastructure.Persistence.Configurations;

public sealed class ContentItemTagConfiguration : IEntityTypeConfiguration<ContentItemTag>
{
    public void Configure(EntityTypeBuilder<ContentItemTag> builder)
    {
        builder.HasKey(tag => new { tag.ContentItemId, tag.TagId });

        builder.HasOne<ContentItem>()
            .WithMany(content => content.Tags)
            .HasForeignKey(tag => tag.ContentItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(tag => tag.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
