using Microsoft.EntityFrameworkCore;

namespace Cmsify.Sqlite.MigrationProbe.Fixtures;

public class UpgradeContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => BuildFixtureModel(modelBuilder, upgraded: true);
    internal static void BuildFixtureModel(ModelBuilder modelBuilder, bool upgraded)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        modelBuilder.Entity<FixtureParent>(b => { b.ToTable("fixture_parents"); b.HasKey(p => p.Id); b.Property(p => p.Id).HasColumnName("id"); });
        modelBuilder.Entity<FixtureChild>(b =>
        {
            b.ToTable("fixture_children"); b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id");
            b.Property(c => c.ParentId).HasColumnName("parent_id");
            b.Property(c => c.Name).HasColumnName("name").IsRequired(upgraded);
            b.HasOne<FixtureParent>().WithMany().HasForeignKey(c => c.ParentId);
            b.HasIndex(c => c.Name).IsUnique();
        });
    }
}
public sealed class OlderUpgradeContext(DbContextOptions<OlderUpgradeContext> options) : UpgradeContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => BuildFixtureModel(modelBuilder, upgraded: false);
}
public sealed class FixtureParent { public int Id { get; set; } }
public sealed class FixtureChild { public int Id { get; set; } public int ParentId { get; set; } public string? Name { get; set; } }
