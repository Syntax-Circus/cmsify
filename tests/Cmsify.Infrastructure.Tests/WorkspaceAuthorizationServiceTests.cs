using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class WorkspaceAuthorizationServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async Task InitializeAsync() => await postgres.StartAsync();

    public async Task DisposeAsync() => await postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Superadmin_CanReadAndWriteEveryWorkspace()
    {
        await using var dbContext = await CreateMigratedContextAsync();
        var workspace = new Workspace { Name = "Restricted", Slug = "restricted" };
        var superadmin = new User
        {
            Email = "host@example.test",
            DisplayName = "Host",
            PasswordHash = "hash",
            Role = UserRole.Admin,
            IsSuperAdmin = true,
            IsActive = true
        };
        dbContext.Workspaces.Add(workspace);
        dbContext.Users.Add(superadmin);
        await dbContext.SaveChangesAsync();

        var service = new WorkspaceAuthorizationService(dbContext, new CurrentActorInfo(superadmin.Id, null, UserRole.Admin, null, true, true));

        Assert.True(await service.CanReadWorkspaceAsync(workspace.Id));
        Assert.True(await service.CanWriteWorkspaceAsync(workspace.Id));
    }

    [Fact]
    public async Task LocalUser_RequiresExplicitReadOrWriteGrant()
    {
        await using var dbContext = await CreateMigratedContextAsync();
        var readWorkspace = new Workspace { Name = "Read", Slug = "read" };
        var writeWorkspace = new Workspace { Name = "Write", Slug = "write" };
        var deniedWorkspace = new Workspace { Name = "Denied", Slug = "denied" };
        var user = new User
        {
            Email = "editor@example.test",
            DisplayName = "Editor",
            PasswordHash = "hash",
            Role = UserRole.Admin,
            IsActive = true
        };
        user.WorkspaceAccesses.Add(new UserWorkspaceAccess { WorkspaceId = readWorkspace.Id, AccessLevel = WorkspaceAccessLevel.Read });
        user.WorkspaceAccesses.Add(new UserWorkspaceAccess { WorkspaceId = writeWorkspace.Id, AccessLevel = WorkspaceAccessLevel.Write });
        dbContext.Workspaces.AddRange(readWorkspace, writeWorkspace, deniedWorkspace);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = new WorkspaceAuthorizationService(dbContext, new CurrentActorInfo(user.Id, null, UserRole.Admin, null, true));

        Assert.True(await service.CanReadWorkspaceAsync(readWorkspace.Id));
        Assert.False(await service.CanWriteWorkspaceAsync(readWorkspace.Id));
        Assert.True(await service.CanReadWorkspaceAsync(writeWorkspace.Id));
        Assert.True(await service.CanWriteWorkspaceAsync(writeWorkspace.Id));
        Assert.False(await service.CanReadWorkspaceAsync(deniedWorkspace.Id));
        Assert.False(await service.CanWriteWorkspaceAsync(deniedWorkspace.Id));
    }

    [Fact]
    public async Task WorkspaceScopedActor_IsLimitedToClaimedWorkspaceAndRole()
    {
        await using var dbContext = await CreateMigratedContextAsync();
        var workspace = new Workspace { Name = "Scoped", Slug = "scoped" };
        var otherWorkspace = new Workspace { Name = "Other", Slug = "other" };
        dbContext.Workspaces.AddRange(workspace, otherWorkspace);
        await dbContext.SaveChangesAsync();

        var readerService = new WorkspaceAuthorizationService(dbContext, new CurrentActorInfo(null, Guid.CreateVersion7(), UserRole.Reader, workspace.Id, true));
        var editorService = new WorkspaceAuthorizationService(dbContext, new CurrentActorInfo(null, Guid.CreateVersion7(), UserRole.Editor, workspace.Id, true));

        Assert.True(await readerService.CanReadWorkspaceAsync(workspace.Id));
        Assert.False(await readerService.CanWriteWorkspaceAsync(workspace.Id));
        Assert.True(await editorService.CanWriteWorkspaceAsync(workspace.Id));
        Assert.False(await editorService.CanReadWorkspaceAsync(otherWorkspace.Id));
    }

    private async Task<CmsifyDbContext> CreateMigratedContextAsync()
    {
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbContext = new CmsifyDbContext(options);
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }
}
