using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NpgsqlTypes;
using System.Text.Json;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresCmsifyProviderModel : ICmsifyProviderModel
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(Entity).IsAssignableFrom(entity.ClrType))
                modelBuilder.Entity(entity.ClrType).Property(nameof(Entity.Id)).HasDefaultValueSql("gen_random_uuid()");
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(JsonElement) || property.ClrType == typeof(JsonElement?))
                    property.SetColumnType("jsonb");
                if (property.Name == "xmin" && property.IsConcurrencyToken)
                    modelBuilder.Entity(entity.ClrType).Property<uint>("xmin")
                        .HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate();
            }
        }
        modelBuilder.Entity<ContentVersion>().Property(version => version.Tags)
            .HasColumnType("text[]").HasConversion(tags => tags.ToArray(), array => array.ToList());
        modelBuilder.Entity<ContentItem>().HasIndex(content => content.SearchVector).HasMethod("GIN");
#pragma warning disable CS0618
        modelBuilder.Entity<ContentItem>().Property(content => content.SearchVector)
            .HasColumnType("tsvector").HasConversion(
                value => NpgsqlTsVector.Parse(value ?? string.Empty), value => value.ToString());
#pragma warning restore CS0618
    }

    public void PrepareChanges(ChangeTracker changeTracker) { }
}
