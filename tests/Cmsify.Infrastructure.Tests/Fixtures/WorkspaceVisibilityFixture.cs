using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests.Fixtures;

internal sealed class WorkspaceVisibilityFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    internal CurrentActorInfo Actor { get; } = new(Guid.NewGuid(), null, UserRole.Admin, null, true);
    internal MutableVisibility Visibility { get; } = new();
    internal MutableCapabilities Capabilities { get; } = new();
    internal CommandCountingInterceptor Commands { get; } = new();
    internal Workspace[] Rows { get; } = Enumerable.Range(0, 5).Select(index => new Workspace
    {
        Name = $"Row {index}", Slug = $"row-{index}"
    }).ToArray();

    internal async Task InitializeAsync(CancellationToken ct)
    {
        await _postgres.StartAsync(ct);
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        await db.Database.MigrateAsync(ct);
        Rows[4].IsDeleted = true;
        Rows[4].DeletedAt = DateTimeOffset.UtcNow;
        db.Workspaces.AddRange(Rows);
        await db.SaveChangesAsync(ct);
        Visibility.Scope = WorkspaceVisibilityScope.ForWorkspaces([Rows[1].Id, Rows[3].Id, Rows[4].Id]);
    }

    internal ServiceProvider Provider(CurrentActorInfo? actor = null, bool defaultVisibility = false)
    {
        var services = SqliteRegistrationTests.Services();
        services.AddScoped<ICurrentActor>(_ => actor ?? Actor);
        if (!defaultVisibility) services.AddScoped<IWorkspaceVisibilityScopeProvider>(_ => Visibility);
        services.AddCmsifyInfrastructure(SqliteRegistrationTests.Configuration(_postgres.GetConnectionString()),
            new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        services.AddScoped<IWorkspaceAuthorizationService>(_ => Capabilities);
        services.AddDbContext<CmsifyDbContext>(options => options.AddInterceptors(Commands));
        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}

internal sealed class MutableVisibility : IWorkspaceVisibilityScopeProvider
{
    internal WorkspaceVisibilityScope Scope { get; set; } = WorkspaceVisibilityScope.None;
    internal Exception? Failure { get; set; }
    internal int Resolutions { get; private set; }
    internal Action? OnResolve { get; set; }
    public Task<WorkspaceVisibilityScope> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Resolutions++;
        OnResolve?.Invoke();
        if (Failure is not null) throw Failure;
        return Task.FromResult(Scope);
    }
}

internal sealed class MutableCapabilities : IWorkspaceAuthorizationService
{
    internal bool Read { get; set; } = true;
    internal bool Write { get; set; } = true;
    public Task<bool> CanReadWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(Read);
    public Task<bool> CanWriteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(Write);
}
