using System.Data.Common;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Infrastructure.Tests.Fixtures;

/// <summary>Real migrated providers, scoped host options and operation fault injection.</summary>
internal sealed class ContentVersionWriteFixture : IAsyncDisposable
{
    private readonly ContentListQueryFixtures _database;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private ContentVersionWriteFixture(ContentListQueryFixtures database, ServiceProvider provider, IServiceScope scope,
        CurrentActorInfo actor, SequenceClock clock, WriteObserver observer)
        => (_database, _provider, _scope, Actor, Clock, Observer) = (database, provider, scope, actor, clock, observer);
    public CurrentActorInfo Actor { get; }
    public SequenceClock Clock { get; }
    public WriteObserver Observer { get; }
    public DateTimeOffset AuditEarliest { get; } = DateTimeOffset.UtcNow;
    public ContentItem Item { get; private set; } = null!;
    public ContentVersion Version { get; private set; } = null!;
    public TemplateField Field { get; private set; } = null!;
    public CmsifyDbContext Caller => _scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
    public IContentVersionEditRepository Repository => _scope.ServiceProvider.GetRequiredService<IContentVersionEditRepository>();
    public IUpdateContentVersionRequestHandler Handler => _scope.ServiceProvider.GetRequiredService<IUpdateContentVersionRequestHandler>();
    public CmsifyDbContext Fresh() => _database.Context();
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static readonly DateTimeOffset LoadedTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task<ContentVersionWriteFixture> Create(bool sqlite)
    {
        var database = await ContentListQueryFixtures.Create(sqlite);
        var services = SqliteRegistrationTests.Services();
        var actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Admin, null, true, true);
        var clock = new SequenceClock();
        var observer = new WriteObserver();
        services.AddScoped<ICurrentActor>(_ => actor);
        services.AddSingleton<TimeProvider>(clock);
        await using (var connection = database.Context())
        {
            var config = SqliteRegistrationTests.Configuration(connection.Database.GetConnectionString()!);
            var options = new CmsifyInfrastructureOptions { Workers = CmsifyWorkers.None, UseHostCurrentActorForAudit = true };
            if (sqlite) services.AddCmsifySqliteInfrastructure(config, options);
            else services.AddCmsifyInfrastructure(config, options);
        }
        // Host-configured interceptor and query behavior must survive construction of owned contexts.
        services.AddDbContext<CmsifyDbContext>(options => options.AddInterceptors(observer).EnableDetailedErrors());
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var fixture = new ContentVersionWriteFixture(database, provider, scope, actor, clock, observer);
        try
        {
            await using var db = fixture.Fresh();
            fixture.Field = new TemplateField { TemplateVersionId = database.TemplateVersion.Id, Key = "body", Label = "Body", PrimitiveType = PrimitiveType.Text, IsRequired = true };
            fixture.Item = database.Item("entry");
            fixture.Item.SearchVector = "'old':1";
            fixture.Item.UpdatedAt = LoadedTime;
            fixture.Version = database.Version(fixture.Item, 1);
            fixture.Version.Status = ContentStatus.Approved;
            fixture.Version.CreatedAt = LoadedTime.AddDays(-1);
            fixture.Version.UpdatedAt = LoadedTime;
            fixture.Version.PublishAt = LoadedTime.AddDays(10);
            fixture.Version.PublishLeaseOwner = "existing-worker";
            fixture.Version.PublishLeaseToken = Guid.NewGuid();
            fixture.Version.PublishLeaseExpiresAt = LoadedTime.AddDays(9);
            fixture.Version.Tags = ["retained-tag"];
            fixture.Version.FieldValues.Add(new() { ContentVersionId = fixture.Version.Id, FieldId = fixture.Field.Id, ValueKind = ValueKind.Text, TextValue = "old" });
            db.AddRange(new User { Id = actor.UserId!.Value, Email = "actor@example.test", DisplayName = "Host actor", PasswordHash = "fixture", Role = UserRole.Admin }, fixture.Field, fixture.Item, fixture.Version);
            await db.SaveChangesAsync(Ct);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public ContentVersionEditValues Values(string text = "saved") => new(LoadedTime.AddDays(1), LoadedTime.AddDays(2),
        [new(Field.Id, 0, ValueKind.Text, text, null, null, null, null, JsonSerializer.SerializeToElement(new { owned = text }))]);
    public UpdateContentVersionRequest Request(long? revision = null) => new(Item.WorkspaceId, Item.Id, 1,
        new(revision ?? LoadedTime.UtcTicks / 10), Values().EffectiveStartAt, Values().EffectiveEndAt, Values().Fields, false);
    public async Task<IContentVersionEditSession> Open() => (await Repository.OpenAsync(Item.WorkspaceId, Item.Id, 1, Ct))!;

    public async Task<string> DurableState()
    {
        await using var db = Fresh();
        var item = await db.ContentItems.AsNoTracking().SingleAsync(x => x.Id == Item.Id, Ct);
        var version = await db.ContentVersions.AsNoTracking().Include(x => x.FieldValues).SingleAsync(x => x.Id == Version.Id, Ct);
        var events = await db.WebhookOutboxEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct);
        var audit = await db.AuditLogs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct);
        return JsonSerializer.Serialize(new { item, version, events, audit });
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    internal sealed class SequenceClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        private int _reads;
        public int Reads => _reads;
        public override DateTimeOffset GetUtcNow() => Start.AddSeconds(Interlocked.Increment(ref _reads));
    }

    internal sealed class WriteObserver : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public bool FailOutbox { get; set; }
        public bool FailProjection { get; set; }
        public bool ArmProjectionAfterSave { get; set; }
        public bool FailOpen { get; set; }
        public CancellationTokenSource? CancelOpen { get; set; }
        public List<CmsifyDbContext> SavingContexts { get; } = [];
        public CmsifyDbContext? LastQueryContext { get; private set; }
        public int OutboxInsertAttempts { get; private set; }
        private void Before(DbCommand command, CommandEventData data)
        {
            LastQueryContext = (CmsifyDbContext)data.Context!;
            if (FailOpen) throw new InvalidOperationException("forced open failure");
            CancelOpen?.Cancel();
            if (command.CommandText.Contains("INSERT INTO webhook_outbox_events", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("INSERT INTO \"webhook_outbox_events\"", StringComparison.OrdinalIgnoreCase))
            {
                OutboxInsertAttempts++;
                if (FailOutbox) throw new InvalidOperationException("forced outbox SQL failure");
            }
            if (FailProjection && command.CommandText.Contains("template_versions", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("forced projection failure");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Before(command, data); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Before(command, data); return ValueTask.FromResult(result); }
        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { SavingContexts.Add((CmsifyDbContext)eventData.Context!); return ValueTask.FromResult(result); }
        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { if (ArmProjectionAfterSave) FailProjection = true; return ValueTask.FromResult(result); }
    }
}
