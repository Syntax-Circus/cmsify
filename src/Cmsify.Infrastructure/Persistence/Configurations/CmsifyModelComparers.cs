using Cmsify.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cmsify.Infrastructure.Persistence.Configurations;

internal static class CmsifyModelComparers
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        // HasConversion can clear the comparer. Apply shared semantics after provider conversion.
        modelBuilder.Entity<ContentVersion>().Property(version => version.Tags).Metadata.SetValueComparer(
            new ValueComparer<IList<string>>(
                (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
                list => list == null ? 0 : list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                list => (IList<string>)list.ToList()));
    }
}
