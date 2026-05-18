using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Persistence;

public sealed class DbSeeder : IDbSeeder
{
    private readonly CmsifyDbContext dbContext;
    private readonly IConfiguration configuration;

    public DbSeeder(CmsifyDbContext dbContext, IConfiguration configuration)
    {
        this.dbContext = dbContext;
        this.configuration = configuration;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!await dbContext.Workspaces.AnyAsync(ct))
        {
            dbContext.Workspaces.Add(new Workspace
            {
                Name = configuration["Seed:DefaultWorkspace:Name"] ?? "Default",
                Slug = configuration["Seed:DefaultWorkspace:Slug"] ?? "default",
                Description = "Default Cmsify workspace"
            });
        }

        if (!await dbContext.Users.AnyAsync(ct))
        {
            var adminEmail = configuration["Auth:Bootstrap:AdminEmail"]
                ?? configuration["Seed:Admin:Email"]
                ?? "admin@localhost";
            var adminPassword = configuration["Auth:Bootstrap:AdminPassword"];
            var passwordHash = !string.IsNullOrWhiteSpace(adminPassword)
                ? BCrypt.Net.BCrypt.HashPassword(adminPassword, configuration.GetValue("Auth:BcryptCost", 12))
                : configuration["Seed:Admin:PasswordHash"];

            if (string.IsNullOrWhiteSpace(passwordHash))
            {
                throw new InvalidOperationException("Auth:Bootstrap:AdminPassword must be configured before the default admin user can be seeded.");
            }

            dbContext.Users.Add(new User
            {
                Email = adminEmail,
                DisplayName = configuration["Auth:Bootstrap:AdminDisplayName"] ?? configuration["Seed:Admin:DisplayName"] ?? "Cmsify Admin",
                PasswordHash = passwordHash,
                Role = UserRole.Admin,
                IsSuperAdmin = true,
                MustChangePassword = true,
                IsActive = true
            });
        }

        await dbContext.SaveChangesAsync(ct);
    }
}
