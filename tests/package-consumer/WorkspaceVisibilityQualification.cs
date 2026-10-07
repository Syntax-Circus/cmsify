using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyntaxCircus.Common;

// Compiled only in isolated package consumers; human composition is separate from operator/content setup.
internal static class WorkspaceVisibilityQualification
{
    private static readonly Guid _hostId = Guid.Parse("629aed91-05d8-4876-a7a8-13bdeabecb15");
    internal static void RegisterHost(IServiceCollection services)
    {
        services.AddScoped<Operation>();
        services.AddScoped<ICurrentActor>(provider => provider.GetRequiredService<Operation>().Actor);
        services.AddScoped<IWorkspaceVisibilityScopeProvider, HostVisibility>();
    }
    internal static void RegisterCapabilities(IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Scoped<IWorkspaceAuthorizationService, HostCapabilities>());

    internal static async Task RunAsync(IServiceProvider services, bool supportsRevisionMutations, CancellationToken ct)
    {
        var rows = Enumerable.Range(0, 5).Select(index => new Workspace { Name = $"Visibility {index}", Slug = $"visibility-{index}" }).ToArray();
        rows[4].IsDeleted = true;
        rows[4].DeletedAt = DateTimeOffset.UtcNow;
        await using (var setup = services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            db.Workspaces.AddRange(rows);
            await db.SaveChangesAsync(ct);
        }
        await using (var reader = services.CreateAsyncScope())
        {
            Initialize(reader.ServiceProvider, UserRole.Reader, [rows[1].Id, rows[3].Id, rows[4].Id], []);
            var actor = reader.ServiceProvider.GetRequiredService<ICurrentActor>();
            Require(actor.UserId == _hostId && actor.ApiClientId is null && actor.WorkspaceId is null && !actor.IsSuperAdmin, "Human identity widened.");
            var get = reader.ServiceProvider.GetRequiredService<IWorkspacesGetRequestHandler>();
            var visible = Success(await get.HandleAsync(new(rows[1].Id), ct));
            Require(!visible.CanWrite, "Visibility conferred write capability.");
            Require((await get.HandleAsync(new(rows[0].Id), ct)).Errors[0].Kind == ResultErrorKind.NotFound, "Cross-workspace read leaked.");
            Require((await get.HandleAsync(new(rows[4].Id), ct)).Errors[0].Kind == ResultErrorKind.NotFound, "Deleted row visible.");
            var repo = reader.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
            var operation = reader.ServiceProvider.GetRequiredService<Operation>();
            var resolutions = operation.Resolutions;
            var first = await repo.ListAsync(new(0, 1), ct);
            Require(first.TotalCount == 2 && first.Items.Single().Id == rows[1].Id && operation.Resolutions == resolutions + 1, "List filtering/count/provider resolution failed.");
            var second = await repo.ListAsync(new(1, 1), ct);
            var beyond = await repo.ListAsync(new(2, 1), ct);
            Require(second.TotalCount == 2 && second.Items.Single().Id == rows[3].Id && beyond.TotalCount == 2 && beyond.Items.Count == 0, "Paging leaked hidden rows/counts.");
            Require((await reader.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(visible.Id, "Reader denied", visible.Slug, null, visible.Revision), ct)).Errors[0].Kind == ResultErrorKind.Forbidden, "Reader role gained mutation.");
            var legacy = new WorkspaceRepository(reader.ServiceProvider.GetRequiredService<CmsifyDbContext>(), actor);
            Require(await legacy.GetAsync(visible.Id, ct) is null, "Legacy constructor stopped using CMS membership.");
        }
        await using (var readOnly = services.CreateAsyncScope())
        {
            Initialize(readOnly.ServiceProvider, UserRole.Admin, [rows[1].Id], []);
            var visible = Success(await readOnly.ServiceProvider.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(rows[1].Id), ct));
            var denied = await readOnly.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(visible.Id, "Capability denied", visible.Slug, null, visible.Revision), ct);
            Require(denied.IsFailure && denied.Errors[0].Kind == ResultErrorKind.NotFound, "Visible Admin bypassed independent write grant.");
        }
        if (supportsRevisionMutations) await QualifyMutationsAsync(services, rows, ct);
        await using (var uninitialized = services.CreateAsyncScope())
        {
            Require((await uninitialized.ServiceProvider.GetRequiredService<IWorkspaceVisibilityScopeProvider>().ResolveAsync(ct)).WorkspaceIds.Count == 0, "Uninitialized provider widened visibility.");
            Require((await uninitialized.ServiceProvider.GetRequiredService<IWorkspaceRepository>().ListAsync(new(), ct)).TotalCount == 0, "Uninitialized human read rows.");
            var db = uninitialized.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            Require(!await db.Users.AnyAsync(ct) && !await db.UserSessions.AnyAsync(ct) && !await db.UserWorkspaceAccesses.AnyAsync(ct) && !await db.ApiClients.AnyAsync(ct), "Host visibility introduced CMS credentials/access rows.");
        }
        Require(typeof(WorkspaceMutationRepository).GetConstructor([typeof(CmsifyDbContext), typeof(ICurrentActor), typeof(IWebhookOutbox)]) is not null, "Old mutation constructor ABI removed.");
        Console.WriteLine($"PASS packed human host visibility, SQL paging/counts, independent capability denial, no CMS access rows; revision mutations={supportsRevisionMutations}.");
    }

    private static async Task QualifyMutationsAsync(IServiceProvider services, Workspace[] rows, CancellationToken ct)
    {
        await using var admin = services.CreateAsyncScope();
        Initialize(admin.ServiceProvider, UserRole.Admin, [rows[1].Id, rows[3].Id], [rows[0].Id, rows[1].Id, rows[3].Id]);
        var repo = admin.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var before = (await repo.GetAsync(rows[1].Id, ct))!;
        var handler = admin.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>();
        var hidden = await handler.HandleAsync(new(rows[0].Id, "Hidden", rows[0].Slug, null, rows[0].UpdatedAt.UtcTicks), ct);
        Require(hidden.IsFailure && hidden.Errors[0].Kind == ResultErrorKind.NotFound, "Write grant bypassed row visibility.");
        var accepted = Success(await handler.HandleAsync(new(before.Id, "Human accepted", before.Slug, null, before.UpdatedAt.UtcTicks), ct));
        Require(accepted.Revision > before.UpdatedAt.UtcTicks, "Accepted host mutation did not advance revision.");
        var mutations = admin.ServiceProvider.GetRequiredService<IWorkspaceMutationRepository>();
        Require((await mutations.UpdateAsync(new(before.Id, "Stale", before.Slug, null), before.UpdatedAt.UtcTicks, ct)).Errors[0].Kind == ResultErrorKind.Conflict, "Stale host update accepted.");
        Require((await mutations.DeleteAsync(before.Id, before.UpdatedAt.UtcTicks, _hostId, ct)).Errors[0].Kind == ResultErrorKind.Conflict, "Stale host delete accepted.");
        var operation = admin.ServiceProvider.GetRequiredService<Operation>();
        operation.Visible = [];
        Require((await mutations.UpdateAsync(new(before.Id, "Revoked", before.Slug, null), accepted.Revision, ct)).Errors[0].Kind == ResultErrorKind.NotFound, "Reused context cached revoked visibility.");
        operation.Visible = [before.Id];
        Require((await admin.ServiceProvider.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(before.Id, accepted.Revision), ct)).IsSuccess, "Host soft deletion failed.");
        await using var verify = services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.IgnoreQueryFilters().SingleAsync(row => row.Id == before.Id, ct);
        Require(stored.Name == "Human accepted" && stored.IsDeleted && stored.DeletedByUserId == _hostId, "Stored host mutation attribution differs.");
        var audit = await db.AuditLogs.Where(row => row.EntityId == before.Id).ToListAsync(ct);
        Require(audit.Count == 3 && audit.Where(row => row.Action != AuditAction.Created).All(row => row.ActorUserId == _hostId && row.ActorApiClientId is null), "Host audit was not atomic/attributed.");
        Require(await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, ct) == 1, "Denied/stale mutations added outbox writes.");
        Require((await db.Workspaces.SingleAsync(row => row.Id == rows[0].Id, ct)).Name == "Visibility 0", "Denied hidden row changed.");
        Require(await db.AuditLogs.CountAsync(row => row.EntityId == rows[0].Id, ct) == 1 && !await db.WebhookOutboxEvents.AnyAsync(row => row.EntityId == rows[0].Id, ct), "Denied hidden mutation wrote audit/outbox.");
    }

    private static void Initialize(IServiceProvider services, UserRole role, Guid[] visible, Guid[] writable)
    {
        var operation = services.GetRequiredService<Operation>();
        operation.Actor = new(_hostId, null, role, null, true);
        operation.Visible = visible;
        operation.Writable = writable;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Success<T>(Result<T> result) { Require(result.IsSuccess, "Human host handler failed."); return result.Value; }
    private sealed class Operation
    {
        public CurrentActorInfo Actor { get; set; } = CurrentActorInfo.Anonymous;
        public Guid[] Visible { get; set; } = [];
        public Guid[] Writable { get; set; } = [];
        public int Resolutions { get; set; }
    }
    private sealed class HostVisibility(Operation operation) : IWorkspaceVisibilityScopeProvider
    {
        public Task<WorkspaceVisibilityScope> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.Resolutions++;
            return Task.FromResult(operation.Actor.IsAuthenticated ? WorkspaceVisibilityScope.ForWorkspaces(operation.Visible) : WorkspaceVisibilityScope.None);
        }
    }
    private sealed class HostCapabilities(Operation operation) : IWorkspaceAuthorizationService
    {
        public Task<bool> CanReadWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(operation.Actor.IsAuthenticated && operation.Visible.Contains(workspaceId));
        public Task<bool> CanWriteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) => Task.FromResult(operation.Actor.IsAuthenticated && operation.Writable.Contains(workspaceId));
    }
}
