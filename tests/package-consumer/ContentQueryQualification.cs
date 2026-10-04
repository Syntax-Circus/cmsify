using System.Diagnostics;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Common;

// One behavioral fixture is copied to both isolated consumers; only composition differs.
internal static class ContentQueryQualification
{
    public static async Task RunAsync(IServiceProvider root, Action<IServiceProvider, Guid> setActor, CancellationToken ct)
    {
        using var http = new HttpProbe();
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var actorId = Guid.Parse("915095f5-975f-4b58-8889-92c912fd70d9");
        setActor(services, actorId);
        var workspace = Success(await services.GetRequiredService<IWorkspacesCreateRequestHandler>()
            .HandleAsync(new("Content query package", "content-query-package", null), ct));
        var db = services.GetRequiredService<CmsifyDbContext>();
        var template = new Template { WorkspaceId = workspace.Id, Name = "Article", Slug = "article" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var owner = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id,
            Slug = "live-owner", LocaleCode = "en", CreatedAt = QualificationClock.At, UpdatedAt = QualificationClock.At };
        var other = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id,
            Slug = "second-owner", CreatedAt = QualificationClock.At.AddDays(-1), UpdatedAt = QualificationClock.At.AddDays(-1) };
        ContentVersion Version(ContentItem item, int number, string slug) => new()
        {
            ContentItemId = item.Id, WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id,
            VersionNumber = number, Slug = slug, LocaleCode = "de", Status = ContentStatus.Published,
            PublishedAt = QualificationClock.At.AddDays(-1), CreatedAt = QualificationClock.At.AddDays(-2),
            UpdatedAt = QualificationClock.At.AddDays(-1)
        };
        var fallback = Version(owner, 1, "fallback-needle"); fallback.Tags = ["zebra"];
        var winner = Version(owner, 2, "snapshot-winner"); winner.Tags = ["alpha", "zebra"];
        winner.EffectiveStartAt = QualificationClock.At.AddHours(-1); winner.EffectiveEndAt = QualificationClock.At.AddHours(1);
        winner.PublishAt = QualificationClock.At.AddDays(-2); winner.PublishedAt = QualificationClock.At;
        winner.RolledBackFromVersionNumber = 7;
        var draft = Version(owner, 3, "draft"); draft.Status = ContentStatus.Draft;
        var second = Version(other, 1, "second-snapshot"); second.Tags = ["alphabet"];
        var alpha = new Tag { WorkspaceId = workspace.Id, Name = "alpha" };
        var zebra = new Tag { WorkspaceId = workspace.Id, Name = "zebra" };
        db.AddRange(template, templateVersion, owner, other, fallback, winner, draft, second, alpha, zebra);
        db.AddRange(new ContentItemTag { ContentItemId = owner.Id, TagId = alpha.Id },
            new ContentItemTag { ContentItemId = owner.Id, TagId = zebra.Id });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var handler = services.GetRequiredService<IListContentRequestHandler>();
        Require(services.GetRequiredService<TimeProvider>() is QualificationClock, "Host clock replaced.");
        var calls = 0;
        async Task<ContentListOutput> Read(ListContentRequest request)
        {
            calls++;
            return Success(await handler.HandleAsync(request, ct));
        }
        var request = new ListContentRequest(workspace.Id);
        var ordinary = await Read(request with { Slug = owner.Slug });
        var item = ordinary.Items.Single();
        Require(ordinary.TotalCount == 1 && item.Id == owner.Id && item.Slug == "live-owner" && item.LocaleCode == "en"
            && item.VersionCount == 3 && item.TemplateName == "Article" && item.TemplateSlug == "article"
            && item.Tags.SequenceEqual(new[] { "alpha", "zebra" }), "Ordinary identity/live tags/version count differ.");
        var serving = item.CurrentlyServingVersion;
        Require(serving is not null && serving.Id == winner.Id && serving.Slug == "snapshot-winner"
            && serving.LocaleCode == "de" && serving.Tags.SequenceEqual(winner.Tags)
            && serving.PublishAt == winner.PublishAt && serving.PublishedAt == winner.PublishedAt
            && serving.RolledBackFromVersionNumber == 7 && serving.CreatedAt == winner.CreatedAt
            && serving.EffectiveStartAt == winner.EffectiveStartAt && serving.EffectiveEndAt == winner.EffectiveEndAt,
            "Ordinary serving snapshot differs.");
        var resolved = await Read(request with { Resolve = true, Slug = winner.Slug });
        item = resolved.Items.Single();
        Require(resolved.TotalCount == 1 && item.Id == owner.Id && item.Slug == winner.Slug && item.LocaleCode == "de"
            && item.VersionCount == 1 && item.CurrentlyServingVersion is null && item.Tags.SequenceEqual(winner.Tags)
            && item.CreatedAt == winner.PublishedAt && item.UpdatedAt == winner.PublishedAt, "Resolved snapshot shape differs.");
        foreach (var resolve in new[] { false, true })
        {
            var mode = request with { Resolve = resolve };
            Require((await Read(mode with { Tags = " ZEBRA,alpha,alpha,, " })).Items.Single().Id == owner.Id,
                "All-tag membership/normalization differs.");
            Require((await Read(mode with { Tags = "alph" })).TotalCount == 0, "Partial tag matched exact membership.");
            var first = await Read(mode with { PageSize = 1 });
            var next = await Read(mode with { PageSize = 1, Page = 2 });
            var past = await Read(mode with { PageSize = 1, Page = 3 });
            Require(first.TotalCount == 2 && next.TotalCount == 2 && past.TotalCount == 2
                && first.Items.Count == 1 && next.Items.Count == 1 && first.Items[0].Id != next.Items[0].Id
                && past.Items.Count == 0 && next.Page == 2 && next.PageSize == 1, "Database paging/count differs.");
            Require((await Read(mode with { Q = "%" })).TotalCount == (resolve ? 0 : 2), "Mode-specific wildcard semantics differ.");
            Require((await Read(mode with { Q = "LIVE-OWNER" })).TotalCount == (resolve ? 0 : 1), "Mode-specific slug search differs.");
        }
        Require((await Read(request with { Resolve = true, Q = "fallback-needle" })).TotalCount == 0,
            "Q incorrectly selected a fallback before winner ranking.");
        Require((await Read(request with { Resolve = true, Slug = fallback.Slug })).Items.Single().Slug == fallback.Slug,
            "Exact candidate filter did not precede ranking.");
        Require((await Read(request with { Resolve = true, Slug = owner.Slug })).TotalCount == 0,
            "Resolved filter used mutable item slug.");
        Require((await Read(request with { Resolve = true, AsOf = QualificationClock.At.AddHours(1), Q = fallback.Slug }))
            .Items.Single().Slug == fallback.Slug, "AsOf/end-exclusive serving boundary differs.");

        foreach (var resolve in new[] { false, true })
        {
            await using var anonymousScope = root.CreateAsyncScope();
            anonymousScope.ServiceProvider.GetRequiredService<ActorScope>().Actor = CurrentActorInfo.Anonymous;
            var anonymous = await anonymousScope.ServiceProvider.GetRequiredService<IListContentRequestHandler>()
                .HandleAsync(request with { Resolve = resolve }, ct); calls++;
            Require(anonymous.IsFailure && anonymous.Errors[0].Kind == ResultErrorKind.Unauthenticated, "Anonymous query accepted.");
            await using var deniedScope = root.CreateAsyncScope();
            deniedScope.ServiceProvider.GetRequiredService<ActorScope>().Actor = new CurrentActorInfo(actorId, null, UserRole.Reader, Guid.NewGuid(), true);
            var hidden = await deniedScope.ServiceProvider.GetRequiredService<IListContentRequestHandler>()
                .HandleAsync(request with { Resolve = resolve }, ct); calls++;
            Require(hidden.IsFailure && hidden.Errors[0].Kind == ResultErrorKind.NotFound, "Workspace denial was not hidden.");
        }
        Require(http.Requests == 0, "Direct content-query workflow made an HTTP request.");
        Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Cmsify.Api"), "API assembly loaded.");
        Require(!await db.Users.AnyAsync(ct) && !await db.UserSessions.AnyAsync(ct) && !await db.ApiClients.AnyAsync(ct),
            "Content workflow required local credentials.");
        Console.WriteLine($"PASS {calls} packaged direct content-query calls: ordinary/resolved snapshots, exact all-tags, Q/ranking/wildcards, paging, fixed clock/AsOf and denial; HTTP requests={http.Requests}; no API assembly/local credentials.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync(ct);
        command.CommandText = db.Database.IsNpgsql()
            ? "SELECT version() || '; lc_collate=' || datcollate || '; lc_ctype=' || datctype FROM pg_database WHERE datname = current_database()"
            : "SELECT sqlite_version()";
        Console.WriteLine($"ENV provider={db.Database.ProviderName}; database={await command.ExecuteScalarAsync(ct)}; runtime={Environment.Version}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    private static T Success<T>(Result<T> result) { Require(result.IsSuccess, "Direct content handler failed."); return result.Value; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class HttpProbe : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        public int Requests { get; private set; }
        public HttpProbe() => _all = DiagnosticListener.AllListeners.Subscribe(this);
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener") _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == "System.Net.Http.HttpRequestOut.Start") Requests++;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void Dispose() { foreach (var subscription in _subscriptions) subscription.Dispose(); _all.Dispose(); }
    }
}

internal sealed class QualificationClock : TimeProvider
{
    public static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => At;
}
