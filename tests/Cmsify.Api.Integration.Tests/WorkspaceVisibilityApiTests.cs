using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class WorkspaceVisibilityApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        ClearEnvironment();
    }

    [Fact]
    public async Task List_OnlyReturnsGrantedWorkspaces_ForLocalUser()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var (grantedWorkspaceId, hiddenWorkspaceId) = await SeedRestrictedUserAsync(factory);
        var login = await LoginAsync(client, "reader@example.test", "reader-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.GetFromJsonAsync<PagedResult<WorkspaceDto>>("/api/v1/workspaces");

        Assert.NotNull(response);
        Assert.Collection(
            response.Items,
            workspace => Assert.Equal(grantedWorkspaceId, workspace.Id));
        Assert.DoesNotContain(response.Items, workspace => workspace.Id == hiddenWorkspaceId);
    }

    [Fact]
    public async Task UnauthorizedWorkspaceEndpoints_ReturnNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var (_, hiddenWorkspaceId) = await SeedRestrictedUserAsync(factory);
        var login = await LoginAsync(client, "reader@example.test", "reader-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using var workspaceResponse = await client.GetAsync($"/api/v1/workspaces/{hiddenWorkspaceId}");
        using var templatesResponse = await client.GetAsync($"/api/v1/workspaces/{hiddenWorkspaceId}/templates");

        Assert.Equal(HttpStatusCode.NotFound, workspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, templatesResponse.StatusCode);
    }

    private static async Task<(Guid GrantedWorkspaceId, Guid HiddenWorkspaceId)> SeedRestrictedUserAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();

        var grantedWorkspace = new Workspace { Name = "Granted Workspace", Slug = $"granted-{Guid.NewGuid():N}" };
        var hiddenWorkspace = new Workspace { Name = "Hidden Workspace", Slug = $"hidden-{Guid.NewGuid():N}" };
        var user = new User
        {
            Email = "reader@example.test",
            DisplayName = "Workspace Reader",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("reader-password", 4),
            Role = UserRole.Reader,
            IsActive = true
        };

        user.WorkspaceAccesses.Add(new UserWorkspaceAccess
        {
            WorkspaceId = grantedWorkspace.Id,
            AccessLevel = WorkspaceAccessLevel.Read
        });

        dbContext.Workspaces.AddRange(grantedWorkspace, hiddenWorkspace);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        return (grantedWorkspace.Id, hiddenWorkspace.Id);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Email", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", null);
    }
}
