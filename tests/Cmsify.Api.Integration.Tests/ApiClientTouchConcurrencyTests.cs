using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ApiClientTouchConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Fact]
    public async Task TrackedTouchAndSave_WhenTwoContextsLoadTheSameXminConcurrencyRow_ThrowsOnTheSecondSave()
    {
        await using var factory = CreateFactory();
        var clientId = await SeedApiClientAsync(factory, "Xmin Race Characterization");

        using var firstScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var firstLoaded = await firstContext.ApiClients.SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken);

        using var secondScope = factory.Services.CreateScope();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var secondLoaded = await secondContext.ApiClients.SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken);

        firstLoaded.LastUsedAt = DateTimeOffset.UtcNow;
        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        secondLoaded.LastUsedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TouchApiClientIfStaleAsync_WhenCalledFromTwoContextsForTheSameStaleClient_NeverThrowsAndTheLaterTimestampWins()
    {
        await using var factory = CreateFactory();
        var clientId = await SeedApiClientAsync(factory, "Xmin Race Fix Verification");
        // Truncated to microseconds: PostgreSQL's timestamptz has microsecond precision, while
        // DateTimeOffset has 100ns ticks, so an untruncated value can round-trip off by one tick.
        var firstTouch = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var secondTouch = firstTouch.AddSeconds(1);

        // A near-zero touch interval keeps both calls "due" independent of the 1-second gap between
        // them, so this isolates the race-safety property (no exception, last write wins) from the
        // separate at-most-once-per-interval throttling behavior, which is exercised elsewhere.
        var touchInterval = TimeSpan.FromTicks(1);

        using var firstScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        await CmsifyOpaqueBearerAuthenticationHandler.TouchApiClientIfStaleAsync(firstContext, clientId, null, firstTouch, touchInterval, TestContext.Current.CancellationToken);

        using var secondScope = factory.Services.CreateScope();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        await CmsifyOpaqueBearerAuthenticationHandler.TouchApiClientIfStaleAsync(secondContext, clientId, null, secondTouch, touchInterval, TestContext.Current.CancellationToken);

        using var verificationScope = factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var persisted = await verificationContext.ApiClients.AsNoTracking().SingleAsync(c => c.Id == clientId, TestContext.Current.CancellationToken);
        Assert.Equal(secondTouch, persisted.LastUsedAt);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);

    private WebApplicationFactory<Program> CreateFactory() => new();

    private static async Task<Guid> SeedApiClientAsync(WebApplicationFactory<Program> factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var client = new ApiClient
        {
            Name = name,
            TokenHash = BCrypt.Net.BCrypt.HashPassword($"cmsify_{Guid.NewGuid():N}", 4),
            Role = UserRole.Reader,
            WorkspaceId = workspaceId,
            CreatedByUserId = adminUserId
        };
        dbContext.ApiClients.Add(client);
        await dbContext.SaveChangesAsync();
        return client.Id;
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
