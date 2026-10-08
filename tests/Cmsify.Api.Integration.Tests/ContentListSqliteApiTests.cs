using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
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
using SyntaxCircus.Cmsify.Contracts;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using ContentStatus = Cmsify.Core.Domain.Enums.ContentStatus;

namespace Cmsify.Api.Integration.Tests;

public sealed class ContentListSqliteApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratedProviderMatchesAuthorizedDirectHandler(bool resolve)
    {
        await using var factory = new ContentListSqliteFactory();
        using var client = await factory.CreateSeededClientAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.Equal(db.Database.GetMigrations(),
            await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        var actor = new CurrentActorInfo(null, factory.ActorId, UserRole.Reader, factory.WorkspaceId, true);
        var authorization = new WorkspaceAuthorizationService(db, actor);
        var handler = new ListContentRequestHandler(scope.ServiceProvider.GetRequiredService<IContentListQueryRepository>(), actor,
            authorization, ContentListSqliteFactory.Clock);
        var direct = await handler.HandleAsync(new(factory.WorkspaceId, Q: "needle", Tags: "snapshot", Resolve: resolve), TestContext.Current.CancellationToken);
        Assert.True(direct.IsSuccess);
        var response = await client.GetAsync(factory.Url($"resolve={resolve}&q=needle&tags=snapshot"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var http = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var item = Assert.Single(http.GetProperty("items").EnumerateArray());
        var expected = Assert.Single(direct.Value.Items);
        Assert.Equal(expected.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(expected.TemplateSlug, item.GetProperty("templateSlug").GetString());
        Assert.Equal(expected.Slug, item.GetProperty("slug").GetString());
        Assert.Equal(expected.CreatedAt, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(expected.UpdatedAt, item.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(expected.Tags, item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());
        Assert.Equal(direct.Value.TotalCount, http.GetProperty("totalCount").GetInt32());
        var expectedPage = JsonSerializer.SerializeToElement(new
        {
            Items = direct.Value.Items.Select(row => new { row.Id, row.TemplateVersionId, row.TemplateName, row.Slug, row.LocaleCode,
                row.TranslationGroupId, row.Tags, row.CreatedAt, row.UpdatedAt, row.VersionCount,
                CurrentlyServingVersion = row.CurrentlyServingVersion is { } version ? new
                { version.Id, version.ContentItemId, version.VersionNumber, Status = version.Status.ToString(), version.TemplateVersionId,
                    version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt, version.PublishedAt,
                    version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber, version.Tags, version.CreatedAt, version.UpdatedAt } : null,
                row.TemplateSlug }), direct.Value.TotalCount, direct.Value.Page, direct.Value.PageSize, TotalPages = 1
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonElement.DeepEquals(expectedPage, http), $"Expected {expectedPage}; actual {http}");
        var deniedId = Guid.NewGuid();
        var denied = await handler.HandleAsync(new(deniedId), TestContext.Current.CancellationToken);
        Assert.Equal(SyntaxCircus.Common.ResultErrorKind.NotFound, Assert.Single(denied.Errors).Kind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{deniedId}/content", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("anonymous", "", 401)]
    [InlineData("anonymous", "page=0", 401)]
    [InlineData("role", "", 403)]
    [InlineData("role", "page=0", 403)]
    [InlineData("workspace", "", 404)]
    [InlineData("workspace", "page=0", 400)]
    [InlineData("allowed", "page=0", 400)]
    [InlineData("allowed", "pageSize=101", 400)]
    [InlineData("allowed", "page=0&pageSize=101&resolve=true", 400)]
    public async Task MiddlewareAndValidationKeepOrdering(string actor, string query, int status)
    {
        await using var factory = new ContentListSqliteFactory();
        using var client = await factory.CreateSeededClientAsync();
        if (actor == "anonymous") client.DefaultRequestHeaders.Authorization = null;
        if (actor == "role") client.DefaultRequestHeaders.Authorization = new("Bearer", ContentListSqliteFactory.DeniedToken);
        var url = actor == "workspace" ? $"/api/v1/workspaces/{Guid.NewGuid()}/content?{query}" : factory.Url(query);
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(url.Split('?')[0], problem.GetProperty("instance").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.True(problem.TryGetProperty("correlationId", out _));
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        if (status == 400) Assert.True(problem.TryGetProperty("errors", out _));
    }
}

// The optional provider is selected only inside this test host. Production Program remains PostgreSQL.
internal sealed class ContentListSqliteFactory(IListContentRequestHandler? handler = null) : WebApplicationFactory<Program>
{
    internal const string Token = "cmsify_content_query_task5_reader";
    internal const string DeniedToken = "cmsify_content_query_task5_denied";
    internal static readonly TimeProvider Clock = new FixedClock();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cmsify-task5-{Guid.NewGuid():N}.db");
    internal Guid WorkspaceId { get; } = Guid.NewGuid();
    internal Guid ActorId { get; } = Guid.NewGuid();
    internal string Url(string query) => $"/api/v1/workspaces/{WorkspaceId}/content?{query}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Cmsify"] = $"Data Source={_path};Pooling=False"
            }).Build();
            var selected = new ServiceCollection();
            selected.AddCmsifySqliteInfrastructure(configuration, new() { Workers = CmsifyWorkers.None });
            // Copy the provider's real DbContext configuration, migrator and query repository;
            // retain the API's real HTTP actor, authentication and resource authorization registrations.
            var providerTypes = selected.Where(descriptor => descriptor.ServiceType == typeof(CmsifyDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions<CmsifyDbContext>)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType.FullName!.StartsWith("Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration", StringComparison.Ordinal)
                || descriptor.ServiceType == typeof(ICmsifyDatabaseMigrator)
                || descriptor.ServiceType == typeof(IContentListQueryRepository)).Select(descriptor => descriptor.ServiceType).ToHashSet();
            foreach (var descriptor in services.Where(descriptor => providerTypes.Contains(descriptor.ServiceType)
                || descriptor.ServiceType == typeof(TimeProvider)
                || descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType?.Namespace == "Cmsify.Infrastructure.BackgroundServices").ToArray())
                services.Remove(descriptor);
            foreach (var descriptor in selected.Where(descriptor => providerTypes.Contains(descriptor.ServiceType))) services.Add(descriptor);
            services.AddSingleton(Clock);
            if (handler is not null)
            {
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IListContentRequestHandler)).ToArray()) services.Remove(descriptor);
                services.AddSingleton(handler);
            }
        });
    }

    internal async Task<HttpClient> CreateSeededClientAsync()
    {
        var client = CreateClient();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.Empty(await db.Users.ToArrayAsync());
        Assert.Empty(await db.Workspaces.ToArrayAsync());
        var user = new User { Email = "task5@example.test", DisplayName = "Task 5", PasswordHash = "unused", Role = UserRole.Reader };
        var workspace = new Workspace { Id = WorkspaceId, Name = "Task 5", Slug = "task5" };
        var template = new Template { WorkspaceId = WorkspaceId, Name = "Task 5 template", Slug = "task5-template" };
        var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1 };
        var tag = new Tag { WorkspaceId = WorkspaceId, Name = "snapshot" };
        var item = new ContentItem { WorkspaceId = WorkspaceId, TemplateVersionId = templateVersion.Id, Slug = "needle" };
        item.Tags.Add(new ContentItemTag { ContentItemId = item.Id, TagId = tag.Id });
        var version = new ContentVersion { ContentItemId = item.Id, WorkspaceId = WorkspaceId, TemplateVersionId = templateVersion.Id,
            VersionNumber = 1, Status = ContentStatus.Published, Slug = "needle", Tags = ["snapshot"], PublishedAt = Clock.GetUtcNow().AddDays(-1) };
        db.AddRange(user, workspace, template, templateVersion, tag, item, version,
            new ApiClient { Id = ActorId, Name = "Task 5 reader", TokenHash = BCrypt.Net.BCrypt.HashPassword(Token, 4),
                Role = UserRole.Reader, WorkspaceId = WorkspaceId, CreatedByUserId = user.Id },
            new ApiClient { Name = "Task 5 denied", TokenHash = BCrypt.Net.BCrypt.HashPassword(DeniedToken, 4),
                Role = (UserRole)(-1), WorkspaceId = WorkspaceId, CreatedByUserId = user.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T12:00:00Z");
    }
}
