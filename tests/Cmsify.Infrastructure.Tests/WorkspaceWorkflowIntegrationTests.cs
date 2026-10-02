using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SyntaxCircus.Common;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class WorkspaceWorkflowIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("workspace_workflows").WithUsername("cmsify").WithPassword("cmsify").Build();
    private readonly CurrentActorInfo _actor = new(Guid.NewGuid(), null, UserRole.Admin, null, true, true);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Ct);
        using var provider = Provider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CmsifyDbContext>().Database.MigrateAsync(Ct);
    }
    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task HostIdentity_WithNoHttpOrLocalCredentialsExecutesAllWorkflowsAndRetainsAuditSubject()
    {
        using var provider = Provider();
        WorkspaceOutput created;
        using (var scope = provider.CreateScope())
        {
            var services = scope.ServiceProvider;
            created = (await services.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("Host workspace", Slug(), null), Ct)).Value;
            created.CanWrite.ShouldBeTrue();
        }
        using (var scope = provider.CreateScope())
        {
            var services = scope.ServiceProvider;
            var read = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), Ct)).Value;
            read.Revision.ShouldBe(created.Revision);
            (await services.GetRequiredService<IWorkspacesListRequestHandler>().HandleAsync(new(1, 100), Ct)).Value.Items.ShouldContain(item => item.Id == created.Id);
            var updated = (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Updated host", created.Slug, null, created.Revision), Ct)).Value;
            updated.Revision.ShouldBeGreaterThan(created.Revision);
            (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, updated.Revision), Ct)).IsSuccess.ShouldBeTrue();
        }
        using var verificationScope = provider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.Users.AnyAsync(Ct)).ShouldBeFalse();
        (await db.UserSessions.AnyAsync(Ct)).ShouldBeFalse();
        var deleted = await db.Workspaces.IgnoreQueryFilters().SingleAsync(item => item.Id == created.Id, Ct);
        deleted.IsDeleted.ShouldBeTrue();
        deleted.DeletedByUserId.ShouldBe(_actor.UserId);
        deleted.UpdatedAt.UtcTicks.ShouldBeGreaterThan(created.Revision);
        var audit = await db.AuditLogs.Where(log => log.EntityId == created.Id).ToListAsync(Ct);
        audit.Count.ShouldBe(3);
        audit.ShouldAllBe(log => log.ActorUserId == _actor.UserId && log.ActorApiClientId == null);
    }

    [Fact]
    public async Task SuccessiveUpdates_RoundTripReturnedRevisionAndCommitExistingOutboxPayload()
    {
        using var provider = Provider();
        var created = await CreateAsync(provider);
        var revision = created.Revision;
        for (var index = 0; index < 3; index++)
        {
            using var scope = provider.CreateScope();
            var updated = (await scope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(created.Id, $"Update {index}", created.Slug, null, revision), Ct)).Value;
            updated.Revision.ShouldBeGreaterThan(revision);
            (updated.Revision % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
            revision = updated.Revision;
        }
        using var verificationScope = provider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.SingleAsync(item => item.Id == created.Id, Ct);
        stored.Name.ShouldBe("Update 2");
        stored.UpdatedAt.UtcTicks.ShouldBe(revision);
        var events = await db.WebhookOutboxEvents.Where(evt => evt.EntityId == created.Id).ToListAsync(Ct);
        events.Count.ShouldBe(3);
        events.ShouldAllBe(evt => evt.EventType == "workspace.updated" && evt.WorkspaceId == created.Id);
        events.Select(evt => evt.Payload.GetProperty("Name").GetString()).ShouldContain("Update 2");
        events.ShouldAllBe(evt => evt.Payload.GetProperty("workspaceId").GetGuid() == created.Id && evt.Payload.GetProperty("Slug").GetString() == created.Slug);
    }

    [Fact]
    public async Task StaleUpdateAndDelete_DoNotChangeWorkspaceAuditOrOutbox()
    {
        using var provider = Provider();
        var created = await CreateAsync(provider);
        long revision;
        using (var scope = provider.CreateScope())
            revision = (await scope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(created.Id, "Winner", created.Slug, null, created.Revision), Ct)).Value.Revision;
        using (var scope = provider.CreateScope())
        {
            var services = scope.ServiceProvider;
            (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Loser", created.Slug, null, created.Revision), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
            (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, created.Revision), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        }
        using var verificationScope = provider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.SingleAsync(item => item.Id == created.Id, Ct);
        stored.Name.ShouldBe("Winner");
        stored.UpdatedAt.UtcTicks.ShouldBe(revision);
        (await db.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task OutboxSaveFailure_RollsBackWorkspaceAndAudit()
    {
        using var provider = Provider();
        var created = await CreateAsync(provider);
        using (var failingProvider = Provider(invalidOutbox: true))
        using (var scope = failingProvider.CreateScope())
            await Should.ThrowAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(created.Id, "Must roll back", created.Slug, null, created.Revision), Ct));
        using var verificationScope = provider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.SingleAsync(item => item.Id == created.Id, Ct);
        stored.Name.ShouldBe(created.Name);
        stored.UpdatedAt.UtcTicks.ShouldBe(created.Revision);
        (await db.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct)).ShouldBe(0);
        (await db.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoContendersForOneRevision_OnlyOneCommits(bool deleteContender)
    {
        using var seedProvider = Provider();
        var created = await CreateAsync(seedProvider);
        using var provider = Provider(gate: new MutationSaveGate());
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
            .HandleAsync(new(created.Id, "First", created.Slug, null, created.Revision), Ct);
        var second = deleteContender
            ? DeleteContenderAsync(secondScope.ServiceProvider, created)
            : UpdateContenderAsync(secondScope.ServiceProvider, created);
        var firstResult = await first;
        var secondResult = await second;
        ((firstResult.IsSuccess ? 1 : 0) + (secondResult.Success ? 1 : 0)).ShouldBe(1);
        var error = firstResult.IsFailure ? firstResult.Errors[0].Kind : secondResult.Error;
        error.ShouldBe(ResultErrorKind.Conflict);
        using var verificationScope = seedProvider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(2);
        var events = await db.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct);
        events.ShouldBe(deleteContender && secondResult.Success ? 0 : 1);
    }

    [Fact]
    public async Task FutureRevision_StillAdvancesWhenClockCannotCatchUp()
    {
        using var provider = Provider();
        var created = await CreateAsync(provider);
        long revision;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var entity = await db.Workspaces.SingleAsync(item => item.Id == created.Id, Ct);
            entity.UpdatedAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
            revision = entity.UpdatedAt.UtcTicks;
            await db.SaveChangesAsync(Ct);
        }
        using var updateScope = provider.CreateScope();
        var updated = (await updateScope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
            .HandleAsync(new(created.Id, created.Name, created.Slug, created.Description, revision), Ct)).Value;
        updated.Revision.ShouldBe(revision + 10);
    }

    [Fact]
    public async Task FailedOutboxSave_AllowsSafeRetryInSameScope()
    {
        using var seedProvider = Provider();
        var created = await CreateAsync(seedProvider);
        using var provider = Provider(invalidOutbox: true);
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>();
        await Should.ThrowAsync<DbUpdateException>(() => handler.HandleAsync(new(created.Id, "Failed", created.Slug, null, created.Revision), Ct));
        var retried = await handler.HandleAsync(new(created.Id, "Retried", created.Slug, null, created.Revision), Ct);
        retried.IsSuccess.ShouldBeTrue();
        using var verificationScope = seedProvider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.Workspaces.SingleAsync(item => item.Id == created.Id, Ct)).Name.ShouldBe("Retried");
        (await db.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task ScopedHostApiActor_UsesExistingScopeAndRetainsSafeMutationDenials()
    {
        using var seedProvider = Provider();
        var created = await CreateAsync(seedProvider);
        var hidden = await CreateAsync(seedProvider);
        var apiActor = new CurrentActorInfo(null, Guid.NewGuid(), UserRole.Admin, created.Id, true);
        using var provider = Provider(actor: apiActor);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), Ct)).Value.CanWrite.ShouldBeTrue();
        var page = (await services.GetRequiredService<IWorkspacesListRequestHandler>().HandleAsync(new(), Ct)).Value;
        page.Items.Single().Id.ShouldBe(created.Id);
        (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(hidden.Id), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await services.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("Workspace", "INVALID", null), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(hidden.Id, "Workspace", "INVALID", null, null), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(hidden.Id, null), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, created.Revision), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        var updated = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Scoped host update", created.Slug, null, created.Revision), Ct);
        updated.IsSuccess.ShouldBeTrue();
        using var verificationScope = seedProvider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.ApiClients.AnyAsync(Ct)).ShouldBeFalse();
        var audit = await db.AuditLogs.SingleAsync(log => log.EntityId == created.Id && log.Action == AuditAction.Updated, Ct);
        audit.ActorApiClientId.ShouldBe(apiActor.ApiClientId);
        audit.ActorUserId.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReusedScope_AfterInterveningWriterAcceptsFreshRevision(bool delete, bool staleFirst)
    {
        using var provider = Provider();
        var created = await CreateAsync(provider);
        var unrelated = await CreateAsync(provider);
        using var firstScope = provider.CreateScope();
        var services = firstScope.ServiceProvider;
        var firstDb = services.GetRequiredService<CmsifyDbContext>();
        var unrelatedTracked = await firstDb.Workspaces.SingleAsync(item => item.Id == unrelated.Id, Ct);
        var firstUpdate = (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>()
            .HandleAsync(new(created.Id, "First scope", created.Slug, null, created.Revision), Ct)).Value;
        WorkspaceOutput otherUpdate;
        using (var otherScope = provider.CreateScope())
            otherUpdate = (await otherScope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>()
                .HandleAsync(new(created.Id, "Other writer", created.Slug, null, firstUpdate.Revision), Ct)).Value;

        unrelatedTracked.Description = "Pending unrelated change";
        firstDb.ChangeTracker.DetectChanges();
        if (staleFirst)
        {
            // Exercise the repository's early revision-conflict return, not just handler prechecks.
            var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
            var error = delete
                ? (await mutations.DeleteAsync(created.Id, created.Revision, _actor.UserId!.Value, Ct)).Errors[0]
                : (await mutations.UpdateAsync(new(created.Id, "Stale", created.Slug, null), created.Revision, Ct)).Errors[0];
            error.Kind.ShouldBe(ResultErrorKind.Conflict);
            firstDb.Entry(unrelatedTracked).State.ShouldBe(EntityState.Modified);
            using var afterStaleScope = provider.CreateScope();
            var afterStaleDb = afterStaleScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            (await afterStaleDb.Workspaces.SingleAsync(item => item.Id == created.Id, Ct)).UpdatedAt.UtcTicks.ShouldBe(otherUpdate.Revision);
            (await afterStaleDb.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(3);
            (await afterStaleDb.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct)).ShouldBe(2);
        }

        var fresh = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(created.Id), Ct)).Value;
        fresh.Revision.ShouldBe(otherUpdate.Revision);
        var succeeded = delete
            ? (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, fresh.Revision), Ct)).IsSuccess
            : (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Fresh retry", created.Slug, null, fresh.Revision), Ct)).IsSuccess;
        succeeded.ShouldBeTrue();
        firstDb.ChangeTracker.Entries<Workspace>().Single(entry => entry.Entity.Id == unrelated.Id).Entity.ShouldBeSameAs(unrelatedTracked);
        firstDb.Entry(unrelatedTracked).State.ShouldBe(EntityState.Unchanged);

        using var verificationScope = provider.CreateScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.IgnoreQueryFilters().SingleAsync(item => item.Id == created.Id, Ct);
        stored.IsDeleted.ShouldBe(delete);
        stored.UpdatedAt.UtcTicks.ShouldBeGreaterThan(otherUpdate.Revision);
        stored.DeletedByUserId.ShouldBe(delete ? _actor.UserId : null);
        stored.Name.ShouldBe(delete ? "Other writer" : "Fresh retry");
        (await db.AuditLogs.CountAsync(log => log.EntityId == created.Id, Ct)).ShouldBe(4);
        (await db.WebhookOutboxEvents.CountAsync(evt => evt.EntityId == created.Id, Ct)).ShouldBe(delete ? 2 : 3);
        (await db.Workspaces.SingleAsync(item => item.Id == unrelated.Id, Ct)).Description.ShouldBe("Pending unrelated change");
    }

    private async Task<WorkspaceOutput> CreateAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("Host workspace", Slug(), null), Ct)).Value;
    }
    private static string Slug() => $"host-{Guid.NewGuid():N}";
    private static async Task<(bool Success, ResultErrorKind? Error)> UpdateContenderAsync(IServiceProvider services, WorkspaceOutput created)
    {
        var result = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(created.Id, "Second", created.Slug, null, created.Revision), Ct);
        return (result.IsSuccess, result.IsFailure ? result.Errors[0].Kind : null);
    }
    private static async Task<(bool Success, ResultErrorKind? Error)> DeleteContenderAsync(IServiceProvider services, WorkspaceOutput created)
    {
        var result = await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(created.Id, created.Revision), Ct);
        return (result.IsSuccess, result.IsFailure ? result.Errors[0].Kind : null);
    }
    private ServiceProvider Provider(bool invalidOutbox = false, MutationSaveGate? gate = null, CurrentActorInfo? actor = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor>(_ => actor ?? _actor);
        services.AddCmsifyInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cmsify"] = _postgres.GetConnectionString()
        }).Build(), new CmsifyInfrastructureOptions { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        if (invalidOutbox) services.AddScoped<IWebhookOutbox, InvalidOutbox>();
        if (gate is not null) services.AddDbContext<CmsifyDbContext>(options => options.AddInterceptors(gate));
        return services.BuildServiceProvider();
    }
    private sealed class InvalidOutbox(CmsifyDbContext db) : IWebhookOutbox
    {
        private bool _failNext = true;
        public void Enqueue(string eventType, Guid? workspaceId, Guid entityId, JsonElement payload, DateTimeOffset occurredAt)
        {
            db.WebhookOutboxEvents.Add(new WebhookOutboxEvent
            {
                EventType = _failNext ? new string('x', 201) : eventType, WorkspaceId = workspaceId, EntityId = entityId, Payload = payload, OccurredAt = occurredAt
            });
            _failNext = false;
        }
    }
    private sealed class MutationSaveGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Workspace>().Any(entry => entry.State == EntityState.Modified))
            {
                if (Interlocked.Increment(ref _count) == 2) _bothReady.SetResult();
                await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
