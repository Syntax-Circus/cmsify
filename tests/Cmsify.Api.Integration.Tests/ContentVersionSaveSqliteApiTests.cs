using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Core.ContentWrites;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Auth;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Sqlite.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;
using CoreStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using CoreRole = Cmsify.Core.Domain.Enums.UserRole;
using CoreKind = Cmsify.Core.Domain.Enums.ValueKind;
using WireRequest = SyntaxCircus.Cmsify.Contracts.UpdateContentVersionRequest;

namespace Cmsify.Api.Integration.Tests;

// SQLite remains opt-in. The identical HTTP/direct matrix also runs against real PostgreSQL.
public sealed class ContentVersionSaveSqliteApiTests
{
    private static readonly JsonSerializerOptions _json = CmsifyJsonOptions.Create();

    [Fact]
    public async Task SaveActionKeepsTheEntireCheckedInOpenApiContract()
    {
        await using var factory = new ContentVersionSaveFactory(true);
        using var client = await factory.CreateSeededClientAsync();
        using var response = await client.GetAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var live = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sdk/typescript/openapi.snapshot.json"))) root = root.Parent;
        root.ShouldNotBeNull();
        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "sdk/typescript/openapi.snapshot.json"), TestContext.Current.CancellationToken));
        JsonElement.DeepEquals(snapshot.RootElement, live).ShouldBeTrue("Live OpenAPI must remain semantically equal to the existing complete public contract.");
    }
    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var sqlite in new[] { true, false })
        foreach (var scenario in new[] { "anonymous", "reader", "hidden", "missing-item", "deleted-item", "wrong-workspace", "missing-version", "published", "archived", "revision", "range-pair", "range-order", "template", "field" })
            yield return [sqlite, scenario];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task MigratedProvidersPreserveDirectAndTransportFailurePrecedence(bool sqlite, string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ContentVersionSaveFactory(sqlite);
        using var client = await factory.CreateSeededClientAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var version = await db.ContentVersions.SingleAsync(v => v.Id == factory.VersionId, ct);
            if (scenario is "published" or "archived") version.Status = scenario == "published" ? CoreStatus.Published : CoreStatus.Archived;
            if (scenario == "deleted-item") (await db.ContentItems.SingleAsync(i => i.Id == factory.ItemId, ct)).IsDeleted = true;
            if (scenario == "template") (await db.TemplateVersions.SingleAsync(t => t.Id == version.TemplateVersionId, ct)).IsDeleted = true;
            await db.SaveChangesAsync(ct);
        }
        var workspace = scenario is "hidden" or "wrong-workspace" ? Guid.NewGuid() : factory.WorkspaceId;
        var item = scenario == "missing-item" ? Guid.NewGuid() : factory.ItemId;
        var number = scenario == "missing-version" ? 99 : 1;
        var start = ContentVersionSaveFactory.Now.AddDays(1);
        var contentStage = scenario is "template" or "field";
        var body = new WireRequest(contentStage ? null : start, scenario == "range-order" ? start : null,
            [new(scenario == "field" ? factory.TextFieldId : Guid.NewGuid(), 0, scenario == "field" ? ValueKind.Boolean : ValueKind.Text, "bad", true, null, null, null, null)]);
        var revision = scenario.StartsWith("range-", StringComparison.Ordinal) || contentStage ? ContentVersionSaveFactory.OriginalAt.UtcTicks / 10 : 0;
        var actor = scenario == "anonymous" ? CurrentActorInfo.Anonymous : new(null, factory.ActorId, scenario == "reader" ? CoreRole.Reader : CoreRole.Editor, factory.WorkspaceId, true);
        using var directScope = factory.Services.CreateScope();
        var directDb = directScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var handler = new UpdateContentVersionRequestHandler(directScope.ServiceProvider.GetRequiredService<IContentVersionEditRepository>(),
            actor, new WorkspaceAuthorizationService(directDb, actor), ContentVersionSaveFactory.Clock);
        var direct = await handler.HandleAsync(new(workspace, item, number, new(revision), body.EffectiveStartAt, body.EffectiveEndAt,
            body.Fields.Select(f => new ContentVersionFieldInput(f.FieldId, f.Order, scenario == "field" ? CoreKind.Boolean : CoreKind.Text,
                f.TextValue, f.BoolValue, f.MediaAssetId, f.FileAssetId, f.ChildContentItemId, f.JsonValue)).ToArray()), ct);
        direct.IsFailure.ShouldBeTrue();
        var (status, code, title, detail) = scenario switch
        {
            "anonymous" => (401, "authentication-required", "Unauthorized", (string?)null),
            "reader" => (403, "forbidden", "Forbidden", null),
            "published" or "archived" => (409, "content-version-not-editable", "Only Draft, Review, or Approved versions can be edited", null),
            "revision" => (412, "concurrency-mismatch", "Concurrency mismatch", null),
            "range-pair" => (422, "invalid-effective-range", "Invalid effective range", "Provide both effectiveStartAt and effectiveEndAt, or neither."),
            "range-order" => (422, "invalid-effective-range", "Invalid effective range", "effectiveStartAt must be before effectiveEndAt."),
            "template" => (422, "content-validation-failed", "Content validation failed", "Template version is unavailable."),
            "field" => (422, "content-validation-failed", "Content validation failed", "Field 'title' expects Text values."),
            _ => (404, "not-found", "Not Found", null)
        };
        direct.Errors.ShouldHaveSingleItem().Code.ShouldBe(code);
        if (scenario == "anonymous") client.DefaultRequestHeaders.Authorization = null;
        if (scenario == "reader") client.DefaultRequestHeaders.Authorization = new("Bearer", ContentVersionSaveFactory.ReaderToken);
        using var response = await SaveAsync(client, $"/api/v1/workspaces/{workspace}/content/{item}/versions/{number}", $"\"{revision}\"", body);
        ((int)response.StatusCode).ShouldBe(status);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        problem.GetProperty("title").GetString().ShouldBe(title);
        if (detail is null) problem.TryGetProperty("detail", out _).ShouldBeFalse();
        else problem.GetProperty("detail").GetString().ShouldBe(detail);
        if (status is 409 or 412 or 422)
        {
            var wireCode = status == 409 ? "conflict" : status == 412 ? "concurrency-mismatch" : "validation-failed";
            problem.GetProperty("type").GetString().ShouldBe("https://cmsify.dev/errors/" + wireCode);
        }
        using var readScope = factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await read.WebhookOutboxEvents.CountAsync(ct)).ShouldBe(0);
        (await read.ContentVersionFieldValues.Where(f => f.ContentVersionId == factory.VersionId).Select(f => f.TextValue).SingleAsync(ct)).ShouldBe("original");
    }

    [Theory]
    [InlineData(true, "", CoreStatus.Draft)] [InlineData(false, "", CoreStatus.Draft)]
    [InlineData(true, "?expandChildren=false", CoreStatus.Review)] [InlineData(false, "?expandChildren=false", CoreStatus.Review)]
    [InlineData(true, "?expandChildren=true", CoreStatus.Approved)] [InlineData(false, "?expandChildren=true", CoreStatus.Approved)]
    public async Task MigratedProvidersMatchCompleteDirectOutputAndDurableSave(bool sqlite, string query, CoreStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ContentVersionSaveFactory(sqlite);
        using var client = await factory.CreateSeededClientAsync();
        await factory.ResetAsync(status);
        var body = new WireRequest(ContentVersionSaveFactory.Now.AddDays(1), ContentVersionSaveFactory.Now.AddDays(2),
            [new(factory.TextFieldId, 3, ValueKind.Text, "replacement", null, null, null, null, null),
             new(factory.ChildFieldId, 5, ValueKind.ChildContent, null, null, null, null, factory.ChildItemId, null)]);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var actor = new CurrentActorInfo(null, factory.ActorId, CoreRole.Editor, factory.WorkspaceId, true);
        var handler = new UpdateContentVersionRequestHandler(scope.ServiceProvider.GetRequiredService<IContentVersionEditRepository>(), actor, new WorkspaceAuthorizationService(db, actor), ContentVersionSaveFactory.Clock);
        var direct = await handler.HandleAsync(new(factory.WorkspaceId, factory.ItemId, 1, new(ContentVersionSaveFactory.OriginalAt.UtcTicks / 10), body.EffectiveStartAt, body.EffectiveEndAt,
            [new(factory.TextFieldId, 3, CoreKind.Text, "replacement", null, null, null, null, null), new(factory.ChildFieldId, 5, CoreKind.ChildContent, null, null, null, null, factory.ChildItemId, null)], query != "?expandChildren=false"), ct);
        direct.IsSuccess.ShouldBeTrue();
        await factory.ResetAsync(status); // identical stored save inputs and identity, using a fresh scope
        using var response = await SaveAsync(client, factory.Url + query, $"\"{ContentVersionSaveFactory.OriginalAt.UtcTicks}\"", body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var http = (await response.Content.ReadFromJsonAsync<ContentVersionDetailResponse>(_json, ct))!;
        ContentVersionSaveAdapterTests.AssertDetail(http, direct.Value.Version);
        response.Headers.ETag!.Tag.ShouldBe("\"63926798400000000\"");
        direct.Value.Revision.ShouldBe(63926798400000000);
        http.TemplateSlug.ShouldBe("node"); http.Status.ToString().ShouldBe(status.ToString()); http.Tags.ShouldBe(["snapshot"]);
        http.Fields.Select(f => f.Order).ShouldBe([3, 5]);
        var child = http.Fields.Single(f => f.Key == "child"); child.ChildContentItemId.ShouldBe(factory.ChildItemId);
        if (query == "?expandChildren=false") child.Child.ShouldBeNull();
        else
        {
            child.Child!.TemplateSlug.ShouldBe("node"); child.Child.Fields.Single(f => f.Key == "title").TextValue.ShouldBe("published child");
            child.Child.Fields.Single(f => f.Key == "child").Child!.Fields.ShouldHaveSingleItem().TextValue.ShouldBe("published grandchild");
        }
        using var readScope = factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var version = await read.ContentVersions.Include(v => v.FieldValues).SingleAsync(v => v.Id == factory.VersionId, ct);
        version.Status.ShouldBe(status); version.PublishAt.ShouldBeNull(); version.PublishLeaseOwner.ShouldBeNull(); version.PublishLeaseToken.ShouldBeNull(); version.PublishLeaseExpiresAt.ShouldBeNull();
        version.UpdatedAt.ShouldBe(ContentVersionSaveFactory.Now); version.FieldValues.Count.ShouldBe(2);
        (await read.ContentItems.SingleAsync(i => i.Id == factory.ItemId, ct)).SearchVector!.ShouldContain("replacement");
        (await read.WebhookOutboxEvents.CountAsync(m => m.EventType == "content.version_updated", ct)).ShouldBe(2);
    }

    public static IEnumerable<object[]> HeaderCases()
    {
        foreach (var sqlite in new[] { true, false })
        foreach (var spelling in new[] { "current", "legacy", "padding", "missing", "blank", "weak", "wildcard", "list", "inner-padding", "leading-zero", "plus-sign", "overflow", "stale" }) yield return [sqlite, spelling];
    }
    [Theory]
    [MemberData(nameof(HeaderCases))]
    public async Task ActualHttpClientPathPreservesCanonicalAndSurroundingWhitespaceSemantics(bool sqlite, string spelling)
    {
        await using var factory = new ContentVersionSaveFactory(sqlite);
        using var client = await factory.CreateSeededClientAsync();
        var number = (ContentVersionSaveFactory.OriginalAt.UtcTicks / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var current = $"\"{number}\"";
        var header = spelling switch
        {
            "current" => current, "legacy" => $"\"{ContentVersionSaveFactory.OriginalAt.UtcTicks}\"", "padding" => " " + current + " ",
            "missing" => null, "blank" => " ", "weak" => "W/" + current, "wildcard" => "*", "list" => current + ", \"0\"",
            "inner-padding" => $"\" {number} \"", "leading-zero" => $"\"0{number}\"", "plus-sign" => $"\"+{number}\"", "overflow" => "\"9223372036854775808\"", _ => "\"0\""
        };
        using var response = await SaveAsync(client, factory.Url, header, new(null, null, []));
        response.StatusCode.ShouldBe(spelling is "current" or "legacy" or "padding" ? HttpStatusCode.OK : HttpStatusCode.PreconditionFailed);
    }

    internal static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string url, string? header, WireRequest body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: _json) };
        if (header is not null) message.Headers.TryAddWithoutValidation("If-Match", header).ShouldBeTrue();
        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }
}

internal sealed class ContentVersionSaveFactory(bool sqlite) : WebApplicationFactory<Program>
{
    internal const string Token = "cmsify_version_save_task5_editor";
    internal const string ReaderToken = "cmsify_version_save_task5_reader";
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
    internal static readonly DateTimeOffset OriginalAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    internal static readonly TimeProvider Clock = new FixedClock();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cmsify-save-task5-{Guid.NewGuid():N}.db");
    private PostgreSqlContainer? _postgres;
    internal Guid WorkspaceId { get; } = Guid.NewGuid(); internal Guid ActorId { get; } = Guid.NewGuid();
    internal Guid ItemId { get; } = Guid.NewGuid(); internal Guid VersionId { get; } = Guid.NewGuid();
    internal Guid TextFieldId { get; } = Guid.NewGuid(); internal Guid ChildFieldId { get; } = Guid.NewGuid(); internal Guid ChildItemId { get; } = Guid.NewGuid();
    internal string Url => $"/api/v1/workspaces/{WorkspaceId}/content/{ItemId}/versions/1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Api:SwaggerEnabled", "true");
        builder.ConfigureServices(services =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Cmsify"] = sqlite ? $"Data Source={_path};Pooling=False" : _postgres!.GetConnectionString() }).Build();
            var selected = new ServiceCollection();
            if (sqlite) selected.AddCmsifySqliteInfrastructure(configuration, new() { Workers = CmsifyWorkers.None });
            else selected.AddCmsifyInfrastructure(configuration, new() { Workers = CmsifyWorkers.None });
            var types = selected.Where(d => d.ServiceType == typeof(CmsifyDbContext) || d.ServiceType == typeof(DbContextOptions<CmsifyDbContext>) || d.ServiceType == typeof(DbContextOptions)
                || d.ServiceType.FullName!.StartsWith("Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration", StringComparison.Ordinal)
                || d.ServiceType == typeof(ICmsifyDatabaseMigrator)).Select(d => d.ServiceType).ToHashSet();
            foreach (var descriptor in services.Where(d => types.Contains(d.ServiceType) || d.ServiceType == typeof(TimeProvider)
                || d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace == "Cmsify.Infrastructure.BackgroundServices").ToArray()) services.Remove(descriptor);
            foreach (var descriptor in selected.Where(d => types.Contains(d.ServiceType))) services.Add(descriptor);
            services.AddSingleton(Clock);
        });
    }

    internal async Task<HttpClient> CreateSeededClientAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!sqlite)
        {
            _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("save_parity").WithUsername("cmsify").WithPassword("cmsify").Build();
            await _postgres.StartAsync(ct);
        }
        var client = CreateClient();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.Database.GetAppliedMigrationsAsync(ct)).ShouldNotBeEmpty();
        var workspace = new Workspace { Id = WorkspaceId, Name = "Save parity", Slug = "save-parity" };
        var user = new User { Email = "save-parity@example.test", DisplayName = "Save parity", PasswordHash = "unused", Role = CoreRole.Editor };
        var template = new Template { WorkspaceId = WorkspaceId, Name = "Node", Slug = "node" };
        var tv = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = Cmsify.Core.Domain.Enums.TemplateVersionStatus.Published };
        var text = new TemplateField { Id = TextFieldId, TemplateVersionId = tv.Id, Key = "title", Label = "Title", PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text };
        var child = new TemplateField { Id = ChildFieldId, TemplateVersionId = tv.Id, Key = "child", Label = "Child", TemplateId = template.Id };
        ContentVersion Node(Guid itemId, Guid versionId, string slug, CoreStatus status, string title)
        {
            var item = new ContentItem { Id = itemId, WorkspaceId = WorkspaceId, TemplateVersionId = tv.Id, Slug = slug, CreatedAt = OriginalAt, UpdatedAt = OriginalAt };
            var version = new ContentVersion { Id = versionId, ContentItemId = item.Id, WorkspaceId = WorkspaceId, TemplateVersionId = tv.Id, VersionNumber = 1,
                Status = status, Slug = slug, LocaleCode = "en-US", Tags = ["snapshot"], CreatedAt = OriginalAt, UpdatedAt = OriginalAt,
                PublishedAt = status == CoreStatus.Published ? OriginalAt : null };
            version.FieldValues.Add(new() { ContentVersionId = version.Id, FieldId = TextFieldId, ValueKind = CoreKind.Text, TextValue = title });
            db.AddRange(item, version); return version;
        }
        db.AddRange(workspace, user, template, tv, text, child);
        Node(ItemId, VersionId, "parent", CoreStatus.Draft, "original");
        var childVersion = Node(ChildItemId, Guid.NewGuid(), "child", CoreStatus.Published, "published child");
        var grandchild = Node(Guid.NewGuid(), Guid.NewGuid(), "grandchild", CoreStatus.Published, "published grandchild");
        childVersion.FieldValues.Add(new() { ContentVersionId = childVersion.Id, FieldId = ChildFieldId, ValueKind = CoreKind.ChildContent, ChildContentItemId = grandchild.ContentItemId, Order = 1 });
        db.AddRange(new ApiClient { Id = ActorId, Name = "Editor", TokenHash = BCrypt.Net.BCrypt.HashPassword(Token, 4), Role = CoreRole.Editor, WorkspaceId = WorkspaceId, CreatedByUserId = user.Id },
            new ApiClient { Name = "Reader", TokenHash = BCrypt.Net.BCrypt.HashPassword(ReaderToken, 4), Role = CoreRole.Reader, WorkspaceId = WorkspaceId, CreatedByUserId = user.Id });
        await db.SaveChangesAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    internal async Task ResetAsync(CoreStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var version = await db.ContentVersions.Include(v => v.FieldValues).SingleAsync(v => v.Id == VersionId, ct);
        db.RemoveRange(version.FieldValues); version.FieldValues.Clear();
        db.ContentVersionFieldValues.Add(new() { ContentVersionId = VersionId, FieldId = TextFieldId, ValueKind = CoreKind.Text, TextValue = "original" });
        version.Status = status; version.UpdatedAt = OriginalAt; version.EffectiveStartAt = null; version.EffectiveEndAt = null; version.UpdatedByUserId = null;
        version.PublishAt = Now.AddDays(3); version.PublishLeaseOwner = "old-owner"; version.PublishLeaseToken = Guid.Parse("44444444-4444-4444-4444-444444444444"); version.PublishLeaseExpiresAt = Now.AddHours(1);
        var item = await db.ContentItems.SingleAsync(i => i.Id == ItemId, ct); item.UpdatedAt = OriginalAt; item.SearchVector = null;
        await db.SaveChangesAsync(ct);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
