using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookProviderPortabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Claims_CompetingWorkersAccountForEligibleWorkInBoundedDisjointBatches(bool sqlite, bool outbox)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        var seed = await SeedAsync(database, 7);
        var pause = new PauseSelection(outbox ? "webhook_outbox_events" : "webhook_delivery_logs", ordered: true);
        var attempted = new ObserveTransactionStart();
        await using var first = database.Context(pause);
        await using var second = database.Context(attempted);
        first.Database.GetDbConnection().ShouldNotBeSameAs(second.Database.GetDbConnection());
        var queryNow = sqlite ? Now.ToOffset(TimeSpan.FromHours(3)) : Now;
        async Task<Guid[]> Claim(CmsifyDbContext context, string owner)
        {
            return outbox
                ? (await Repository(context).ClaimOutboxEventsAsync(owner, queryNow, Lease, 3, Ct)).Select(claim => claim.Id).ToArray()
                : (await Repository(context).ClaimPendingDeliveryLogsAsync(owner, queryNow, Lease, 3, Ct)).Select(claim => claim.Id).ToArray();
        }
        var firstTask = Task.Run(() => Claim(first, "one"), Ct);
        Task<Guid[]>? secondTask = null;
        Guid[][] batches;
        try
        {
            await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            pause.Connection.ShouldBeSameAs(first.Database.GetDbConnection());
            pause.Transaction.ShouldNotBeNull();
            secondTask = Task.Run(() => Claim(second, "two"), Ct);
            await attempted.Starting.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            if (sqlite)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
                secondTask.IsCompleted.ShouldBeFalse();
            }
            else
                (await secondTask.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Length.ShouldBe(3);
        }
        finally
        {
            pause.Release.TrySetResult();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (secondTask is not null) await secondTask.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        batches = [await firstTask, await secondTask!];
        batches.ShouldAllBe(batch => batch.Length == 3);
        batches[0].ShouldBe((outbox ? seed.Events.Select(evt => evt.Id) : seed.Deliveries.Select(log => log.Id)).Reverse().Take(3));
        var ids = batches.SelectMany(batch => batch).ToList();
        ids.Distinct().Count().ShouldBe(6);
        await using var remaining = database.Context();
        ids.AddRange(await Claim(remaining, "three"));
        ids.Count.ShouldBe(7);
        ids.Order().ShouldBe((outbox ? seed.Events.Select(evt => evt.Id) : seed.Deliveries.Select(log => log.Id)).Order());
        await using var verification = database.Context();
        if (outbox)
            (await verification.WebhookOutboxEvents.CountAsync(evt => evt.LeaseToken != null, Ct)).ShouldBe(7);
        else
            (await verification.WebhookDeliveryLogs.CountAsync(log => log.LeaseToken != null, Ct)).ShouldBe(7);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Claims_CommandCountIsIndependentOfEligibleQueueDepth(bool sqlite, bool outbox)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        var seed = await SeedAsync(database, 3);
        async Task<int> Measure(DateTimeOffset now)
        {
            var counter = new CommandCountingInterceptor();
            await using var worker = database.Context(counter);
            using (counter.BeginMeasurement())
            {
                if (outbox)
                    (await Repository(worker).ClaimOutboxEventsAsync("counter", now, Lease, 3, Ct)).Count.ShouldBe(3);
                else
                    (await Repository(worker).ClaimPendingDeliveryLogsAsync("counter", now, Lease, 3, Ct)).Count.ShouldBe(3);
            }
            counter.CommandCount.ShouldBeGreaterThan(0);
            return counter.CommandCount;
        }
        var baseline = await Measure(Now);
        await using (var setup = database.Context())
        {
            for (var index = 0; index < 30; index++)
            {
                var evt = new WebhookOutboxEvent { EventType = "content.changed", WorkspaceId = seed.Active.WorkspaceId, Payload = seed.Events[0].Payload, OccurredAt = Now, CreatedAt = Now };
                setup.Add(evt);
                setup.Add(new WebhookDeliveryLog { WebhookEventId = evt.Id, WebhookEndpointId = seed.Active.Id, EventType = evt.EventType, Payload = evt.Payload, NextRetryAt = Now, CreatedAt = Now });
            }
            await setup.SaveChangesAsync(Ct);
        }
        (await Measure(Now.AddMinutes(2))).ShouldBe(baseline);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Claims_EmptyQueuesAndInvalidBoundsDoNotMutateWork(bool sqlite)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        await using var context = database.Context();
        var repository = Repository(context);
        foreach (var (limit, duration) in new[] { (1, TimeSpan.FromSeconds(1)), (500, TimeSpan.FromMinutes(30)) })
        {
            (await repository.ClaimOutboxEventsAsync("worker", Now, duration, limit, Ct)).ShouldBeEmpty();
            (await repository.ClaimPendingDeliveryLogsAsync("worker", Now, duration, limit, Ct)).ShouldBeEmpty();
        }
        foreach (var limit in new[] { 0, 501 })
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.ClaimOutboxEventsAsync("worker", Now, Lease, limit, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.ClaimPendingDeliveryLogsAsync("worker", Now, Lease, limit, Ct));
        }
        foreach (var duration in new[] { TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)), TimeSpan.FromMinutes(30).Add(TimeSpan.FromTicks(1)) })
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.ClaimOutboxEventsAsync("worker", Now, duration, 1, Ct));
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.ClaimPendingDeliveryLogsAsync("worker", Now, duration, 1, Ct));
        }
        foreach (var owner in new[] { "", " ", new string('x', 201) })
        {
            await Should.ThrowAsync<ArgumentException>(() => repository.ClaimOutboxEventsAsync(owner, Now, Lease, 1, Ct));
            await Should.ThrowAsync<ArgumentException>(() => repository.ClaimPendingDeliveryLogsAsync(owner, Now, Lease, 1, Ct));
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => repository.ClaimOutboxEventsAsync("worker", Now, Lease, 1, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => repository.ClaimPendingDeliveryLogsAsync("worker", Now, Lease, 1, canceled.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaseFences_RejectInvalidExpiredSupersededAndCompletedClaims(bool sqlite)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        await SeedAsync(database, 1);
        await using var original = database.Context();
        var evt = (await Repository(original).ClaimOutboxEventsAsync("original", Now, Lease, 1, Ct)).Single();
        var delivery = (await Repository(original).ClaimPendingDeliveryLogsAsync("original", Now, Lease, 1, Ct)).Single();
        async Task<bool> Materialize(ClaimedWebhookOutboxEventDto claim, DateTimeOffset at)
        {
            await using var context = database.Context();
            return await Repository(context).MaterializeOutboxEventAsync(claim, at, Ct);
        }
        async Task<bool> Complete(PendingWebhookDeliveryDto claim, DateTimeOffset at, bool failure = false)
        {
            await using var context = database.Context();
            var completion = new WebhookDeliveryCompletionDto(claim.Id, claim.LeaseOwner, claim.LeaseToken, at);
            return failure
                ? await Repository(context).CompleteDeliveryFailedAsync(completion, 503, "retry", at.AddMinutes(1), false, Ct)
                : await Repository(context).CompleteDeliverySucceededAsync(completion, 204, Ct);
        }
        (await Materialize(evt with { LeaseOwner = "wrong" }, Now)).ShouldBeFalse();
        (await Materialize(evt with { LeaseToken = Guid.NewGuid() }, Now)).ShouldBeFalse();
        (await Complete(delivery with { LeaseOwner = "wrong" }, Now)).ShouldBeFalse();
        (await Complete(delivery with { LeaseToken = Guid.NewGuid() }, Now, true)).ShouldBeFalse();
        (await Materialize(evt, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(delivery, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(delivery, Now.Add(Lease), true)).ShouldBeFalse();
        await using var reclaim = database.Context();
        (await Repository(reclaim).ClaimOutboxEventsAsync("new", Now.Add(Lease).AddTicks(-10), Lease, 1, Ct)).ShouldBeEmpty();
        (await Repository(reclaim).ClaimPendingDeliveryLogsAsync("new", Now.Add(Lease).AddTicks(-10), Lease, 1, Ct)).ShouldBeEmpty();
        var newEvent = (await Repository(reclaim).ClaimOutboxEventsAsync("new", Now.Add(Lease), Lease, 1, Ct)).Single();
        var newDelivery = (await Repository(reclaim).ClaimPendingDeliveryLogsAsync("new", Now.Add(Lease), Lease, 1, Ct)).Single();
        newEvent.LeaseToken.ShouldNotBe(evt.LeaseToken);
        newDelivery.LeaseToken.ShouldNotBe(delivery.LeaseToken);
        newEvent.WasReclaimed.ShouldBeTrue();
        newDelivery.WasReclaimed.ShouldBeTrue();
        (await Materialize(evt, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(delivery, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(delivery, Now.Add(Lease), true)).ShouldBeFalse();
        (await Materialize(newEvent, Now.Add(Lease))).ShouldBeTrue();
        (await Materialize(newEvent, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(newDelivery, Now.Add(Lease))).ShouldBeTrue();
        (await Complete(newDelivery, Now.Add(Lease))).ShouldBeFalse();
        (await Complete(newDelivery, Now.Add(Lease), true)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialization_ManualLiveLeaseCreatesOnlyMatchingIntentsAndPreservesDeduplication(bool sqlite)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        var seed = await SeedAsync(database, 1, manualLease: true);
        await using var context = database.Context();
        var claim = Claim(seed.Events.Single());
        var queryNow = sqlite ? Now.ToOffset(TimeSpan.FromHours(3)) : Now;
        (await Repository(context).MaterializeOutboxEventAsync(claim, queryNow, Ct)).ShouldBeTrue();
        (await Repository(context).MaterializeOutboxEventAsync(claim, Now, Ct)).ShouldBeFalse();
        await using var verification = database.Context();
        var deliveries = await verification.WebhookDeliveryLogs.Where(log => log.WebhookEventId == claim.Id).ToListAsync(Ct);
        deliveries.Select(log => log.WebhookEndpointId).Order().ShouldBe(new[] { seed.Active.Id, seed.SecondActive.Id }.Order());
        deliveries.ShouldAllBe(log => log.EventType == "content.changed" && log.NextRetryAt == Now && log.Payload.GetProperty("value").GetInt32() == 42);
        var evt = await verification.WebhookOutboxEvents.SingleAsync(candidate => candidate.Id == claim.Id, Ct);
        evt.ProcessedAt.ShouldBe(Now);
        evt.LeaseOwner.ShouldBeNull();
        evt.LeaseToken.ShouldBeNull();
        evt.LeaseExpiresAt.ShouldBeNull();
        verification.WebhookDeliveryLogs.Add(new WebhookDeliveryLog { WebhookEventId = claim.Id, WebhookEndpointId = seed.Active.Id, EventType = claim.EventType, Payload = claim.Payload });
        await Should.ThrowAsync<DbUpdateException>(() => verification.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialization_DatabaseFailureRollsBackIntentsAndCompletionThenRetrySucceeds(bool sqlite)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        var seed = await SeedAsync(database, 1, manualLease: true);
        var claim = Claim(seed.Events.Single());
        await using (var setup = database.Context())
        {
            // Fail the outbox completion in the database, inside the actual materialization transaction.
            await setup.Database.ExecuteSqlRawAsync(sqlite
                ? "CREATE TRIGGER reject_completion BEFORE UPDATE OF processed_at ON webhook_outbox_events WHEN NEW.processed_at IS NOT NULL BEGIN SELECT RAISE(ABORT, 'forced completion failure'); END;"
                : "ALTER TABLE webhook_outbox_events ADD CONSTRAINT reject_completion CHECK (processed_at IS NULL) NOT VALID", Ct);
        }
        await using (var materializer = database.Context())
            await Should.ThrowAsync<DbUpdateException>(() => Repository(materializer).MaterializeOutboxEventAsync(claim, Now, Ct));
        await using (var verification = database.Context())
        {
            (await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == claim.Id, Ct)).ShouldBe(1);
            var evt = await verification.WebhookOutboxEvents.SingleAsync(candidate => candidate.Id == claim.Id, Ct);
            evt.ProcessedAt.ShouldBeNull();
            evt.LeaseToken.ShouldBe(claim.LeaseToken);
            await verification.Database.ExecuteSqlRawAsync(sqlite ? "DROP TRIGGER reject_completion" : "ALTER TABLE webhook_outbox_events DROP CONSTRAINT reject_completion", Ct);
        }
        await using (var retry = database.Context())
            (await Repository(retry).MaterializeOutboxEventAsync(claim, Now, Ct)).ShouldBeTrue();
        await using var final = database.Context();
        (await final.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == claim.Id, Ct)).ShouldBe(2);
        (await final.WebhookOutboxEvents.SingleAsync(evt => evt.Id == claim.Id, Ct)).ProcessedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialization_HoldsLeaseFenceThroughCommitAgainstExpiredLeaseReclaim(bool sqlite)
    {
        await using var database = await TestDatabase.CreateAsync(sqlite);
        var seed = await SeedAsync(database, 1, manualLease: true);
        var pause = new PauseSelection("webhook_outbox_events", ordered: false);
        var attempted = new ObserveTransactionStart();
        await using var materializer = database.Context(pause);
        await using var reclaimer = database.Context(attempted);
        var completing = Task.Run(() => Repository(materializer).MaterializeOutboxEventAsync(Claim(seed.Events.Single()), Now, Ct), Ct);
        Task<IReadOnlyList<ClaimedWebhookOutboxEventDto>>? reclaiming = null;
        try
        {
            await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            pause.Connection.ShouldBeSameAs(materializer.Database.GetDbConnection());
            pause.Transaction.ShouldNotBeNull();
            reclaiming = Task.Run(() => Repository(reclaimer).ClaimOutboxEventsAsync("reclaimer", Now.AddMinutes(2), Lease, 1, Ct), Ct);
            await attempted.Starting.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            if (sqlite)
            {
                // SQLite's second writer must wait for the reservation. PostgreSQL skips this row.
                await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
                reclaiming.IsCompleted.ShouldBeFalse();
            }
            else
                (await reclaiming.WaitAsync(TimeSpan.FromSeconds(15), Ct)).ShouldBeEmpty();
        }
        finally
        {
            pause.Release.TrySetResult();
            (await completing.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
            if (reclaiming is not null)
                (await reclaiming.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBeEmpty();
        }
        await using var verification = database.Context();
        (await verification.WebhookDeliveryLogs.CountAsync(log => log.WebhookEventId == seed.Events.Single().Id, Ct)).ShouldBe(2);
    }

    private static WebhookRepository Repository(CmsifyDbContext context)
    {
        var protector = Substitute.For<ISecretProtector>();
        protector.Unprotect(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new(context, CurrentActorInfo.Anonymous, protector, Options.Create(new SecretProtectionOptions()));
    }

    private static ClaimedWebhookOutboxEventDto Claim(WebhookOutboxEvent evt) =>
        new(evt.Id, evt.EventType, evt.WorkspaceId, evt.EntityId, evt.Payload, evt.OccurredAt, evt.LeaseOwner!, evt.LeaseToken!.Value);

    private sealed record Seed(WebhookEndpoint Active, WebhookEndpoint SecondActive, WebhookOutboxEvent[] Events, WebhookDeliveryLog[] Deliveries);

    private static async Task<Seed> SeedAsync(TestDatabase database, int count, bool manualLease = false)
    {
        await using var context = database.Context();
        var workspace = new Workspace { Name = "Webhooks", Slug = "webhooks" };
        var otherWorkspace = new Workspace { Name = "Other", Slug = "other" };
        var user = new User { Email = "webhook@example.test", DisplayName = "Webhook", PasswordHash = "hash", Role = UserRole.Admin };
        WebhookEndpoint Endpoint(string name, Guid workspaceId, bool active = true, bool deleted = false, string eventType = "content.changed")
        {
            var endpoint = new WebhookEndpoint { WorkspaceId = workspaceId, Name = name, Url = "https://example.test/hook", Secret = "ciphertext", CreatedByUserId = user.Id, IsActive = active, IsDeleted = deleted };
            endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = eventType });
            return endpoint;
        }
        var active = Endpoint("active", workspace.Id);
        var second = Endpoint("second", workspace.Id);
        var inactive = Endpoint("inactive", workspace.Id, active: false);
        var deleted = Endpoint("deleted", workspace.Id, deleted: true);
        var unrelated = Endpoint("unrelated", workspace.Id, eventType: "other.event");
        var other = Endpoint("other workspace", otherWorkspace.Id);
        var payload = JsonSerializer.SerializeToElement(new { value = 42 });
        var events = Enumerable.Range(0, count).Select(index => new WebhookOutboxEvent
        {
            EventType = "content.changed", WorkspaceId = workspace.Id, Payload = payload, OccurredAt = Now.AddSeconds(-index), CreatedAt = Now,
            LeaseOwner = manualLease ? "manual" : null, LeaseToken = manualLease ? Guid.NewGuid() : null, LeaseExpiresAt = manualLease ? Now.Add(Lease) : null
        }).ToArray();
        // The first pre-existing intent must survive materialization without being duplicated.
        var deliveries = events.Select(evt => new WebhookDeliveryLog { WebhookEventId = evt.Id, WebhookEndpointId = active.Id, EventType = evt.EventType, Payload = payload, CreatedAt = Now, NextRetryAt = evt.OccurredAt }).ToArray();
        context.AddRange(workspace, otherWorkspace, user, active, second, inactive, deleted, unrelated, other);
        context.AddRange(events);
        context.AddRange(deliveries);
        foreach (var endpoint in new[] { inactive, deleted })
            context.Add(new WebhookDeliveryLog { WebhookEventId = Guid.NewGuid(), WebhookEndpointId = endpoint.Id, EventType = "content.changed", Payload = payload, CreatedAt = Now, NextRetryAt = Now });
        context.Add(new WebhookDeliveryLog { WebhookEventId = Guid.NewGuid(), WebhookEndpointId = active.Id, EventType = "content.changed", Payload = payload, CreatedAt = Now, NextRetryAt = Now.AddHours(1) });
        context.Add(new WebhookDeliveryLog { WebhookEventId = Guid.NewGuid(), WebhookEndpointId = active.Id, EventType = "content.changed", Payload = payload, CreatedAt = Now, NextRetryAt = Now, IsDelivered = true });
        context.Add(new WebhookDeliveryLog { WebhookEventId = Guid.NewGuid(), WebhookEndpointId = active.Id, EventType = "content.changed", Payload = payload, CreatedAt = Now, NextRetryAt = Now, IsFailed = true });
        context.Add(new WebhookOutboxEvent { EventType = "content.changed", Payload = payload, OccurredAt = Now, CreatedAt = Now, ProcessedAt = Now });
        await context.SaveChangesAsync(Ct);
        return new(active, second, events, deliveries);
    }

    private sealed class TestDatabase(string? path, PostgreSqlContainer? postgres, DbContextOptions<CmsifyDbContext> options) : IAsyncDisposable
    {
        public CmsifyDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<CmsifyDbContext>(options).AddInterceptors(interceptors).Options);

        public static async Task<TestDatabase> CreateAsync(bool sqlite)
        {
            string? path = null;
            PostgreSqlContainer? postgres = null;
            var builder = new DbContextOptionsBuilder<CmsifyDbContext>();
            if (sqlite)
            {
                path = Path.Combine(Path.GetTempPath(), $"cmsify-webhook-{Guid.NewGuid():N}.db");
                builder.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention();
            }
            else
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("webhooks").WithUsername("cmsify").WithPassword("cmsify").Build();
                await postgres.StartAsync(Ct);
                builder.UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention();
            }
            var database = new TestDatabase(path, postgres, builder.Options);
            await using var setup = database.Context();
            if (sqlite) await setup.Database.EnsureCreatedAsync(Ct);
            else await setup.Database.MigrateAsync(Ct);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            if (postgres is not null) await postgres.DisposeAsync();
            if (path is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private sealed class PauseSelection(string table, bool ordered) : DbCommandInterceptor
    {
        public TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbConnection? Connection { get; private set; }
        public DbTransaction? Transaction { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase)
                && (ordered || command.CommandText.Contains("lease_owner", StringComparison.OrdinalIgnoreCase))
                && command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) == ordered
                && !Selected.Task.IsCompleted)
            {
                Connection = command.Connection;
                Transaction = command.Transaction;
                Selected.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObserveTransactionStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Starting.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
