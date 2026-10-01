using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteCmsifyProviderModel : ICmsifyProviderModel
{
    public void Configure(ModelBuilder modelBuilder)
    {
        var json = new ValueConverter<JsonElement, string>(
            value => value.GetRawText(), value => JsonSerializer.Deserialize<JsonElement>(value, (JsonSerializerOptions?)null));
        var utcTicks = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(JsonElement) || property.ClrType == typeof(JsonElement?))
                {
                    property.SetValueConverter(json);
                    property.SetColumnType("TEXT");
                }
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(utcTicks);
                // Preserve existing infrastructure/ETag readers while keeping this off entities.
                if (property.Name == "xmin" && property.IsConcurrencyToken)
                    modelBuilder.Entity(entity.ClrType).Property<uint>("xmin")
                        .HasColumnName("row_version").ValueGeneratedNever();
            }
        }
        modelBuilder.Entity<ContentVersion>().Property(version => version.Tags)
            .HasColumnType("TEXT").HasConversion(
                tags => JsonSerializer.Serialize(tags, (JsonSerializerOptions?)null),
                jsonText => JsonSerializer.Deserialize<List<string>>(jsonText, (JsonSerializerOptions?)null)!);
    }

    public void PrepareChanges(ChangeTracker changeTracker)
    {
        changeTracker.DetectChanges();
        foreach (var entry in changeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Metadata.FindProperty("xmin") is not { IsConcurrencyToken: true }) continue;
            var version = entry.Property("xmin");
            version.CurrentValue = entry.State == EntityState.Added ? 1u : checked((uint)version.OriginalValue! + 1u);
        }
    }
}
