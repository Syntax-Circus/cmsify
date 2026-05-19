using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cmsify.Infrastructure.Persistence.Interceptors;

public static class AuditDeltaBuilder
{
    public static JsonElement? Build(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsShadowProperty() && property.Metadata.Name == "xmin")
            {
                continue;
            }

            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added)
            {
                changes[property.Metadata.Name] = new { after = property.CurrentValue };
            }
            else if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Deleted)
            {
                changes[property.Metadata.Name] = new { before = property.OriginalValue };
            }
            else if (property.IsModified)
            {
                changes[property.Metadata.Name] = new
                {
                    before = property.OriginalValue,
                    after = property.CurrentValue
                };
            }
        }

        if (changes.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(changes);
    }
}
