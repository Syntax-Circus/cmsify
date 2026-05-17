namespace Cmsify.Infrastructure.Persistence;

public interface IDbSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}
