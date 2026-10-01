using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cmsify.Infrastructure.Persistence.Providers;

// Persistence-only extension point. Domain entities never select a database.
internal interface ICmsifyProviderModel
{
    void Configure(ModelBuilder modelBuilder);
    void PrepareChanges(ChangeTracker changeTracker);
}
