using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

        var response = await client.GetFromJsonAsync<SyntaxCircus.Cmsify.Contracts.PagedResponse<WorkspaceDto>>("/api/v1/workspaces?page=1&pageSize=10");

        Assert.NotNull(response);
        Assert.Collection(
            response.Items,
            workspace =>
            {
                Assert.Equal(grantedWorkspaceId, workspace.Id);
                Assert.False(workspace.CanWrite);
            });
        Assert.DoesNotContain(response.Items, workspace => workspace.Id == hiddenWorkspaceId);
    }

    [Fact]
    public async Task List_ReportsWriteCapability_ForGrantedWriter()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var workspaceId = await SeedWorkspaceWriterAsync(factory);
        var login = await LoginAsync(client, "writer@example.test", "writer-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.GetFromJsonAsync<SyntaxCircus.Cmsify.Contracts.PagedResponse<WorkspaceDto>>("/api/v1/workspaces?page=1&pageSize=10");

        Assert.NotNull(response);
        Assert.Equal(1, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(1, response.TotalPages);
        Assert.Equal(1, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(1, response.TotalPages);
        Assert.Collection(
            response.Items,
            workspace =>
            {
                Assert.Equal(workspaceId, workspace.Id);
                Assert.True(workspace.CanWrite);
            });

        var workspace = await client.GetFromJsonAsync<WorkspaceDto>($"/api/v1/workspaces/{workspaceId}");
        Assert.NotNull(workspace);
        Assert.True(workspace.CanWrite);
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

    [Fact]
    public async Task CreateUser_AcceptsStringEnumValues_FromThePublicWireContract()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "admin@example.test", "change-this-temporary-password");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        using var request = new StringContent(
            """{"email":"editor@example.test","displayName":"Editor","role":"Editor","temporaryPassword":"temporary-password","isSuperAdmin":false,"workspaceAccesses":[]}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/api/v1/users", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static async Task<Guid> SeedWorkspaceWriterAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();

        var workspace = new Workspace { Name = "Writable Workspace", Slug = $"writable-{Guid.NewGuid():N}" };
        var user = new User
        {
            Email = "writer@example.test",
            DisplayName = "Workspace Writer",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("writer-password", 4),
            Role = UserRole.Admin,
            IsActive = true
        };

        user.WorkspaceAccesses.Add(new UserWorkspaceAccess
        {
            WorkspaceId = workspace.Id,
            AccessLevel = WorkspaceAccessLevel.Write
        });

        dbContext.Workspaces.Add(workspace);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        return workspace.Id;
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
