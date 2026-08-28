using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class ResolvedContentListQueryTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_resolved_query_test_token";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        ClearEnvironment();
    }

    [Fact]
    public async Task ValidResolvedList_UsesOneCountAndOneJoinedPageCommand()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page=1&pageSize=2",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, response.GetProperty("items").GetArrayLength());
        Assert.Equal(2, commands.Commands.Count);
        Assert.Contains("content_versions", commands.Commands[0], StringComparison.Ordinal);
        Assert.Contains(" join ", commands.Commands[1], StringComparison.Ordinal);
        Assert.Contains("template_versions", commands.Commands[1], StringComparison.Ordinal);
        Assert.Contains("templates", commands.Commands[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResolvedList_CommandCountDoesNotGrowWithTheNumberOfPageResults(int pageSize)
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page=1&pageSize={pageSize}",
            TestContext.Current.CancellationToken);

        Assert.Equal(pageSize, response.GetProperty("items").GetArrayLength());
        Assert.Equal(2, commands.Commands.Count);
    }

    [Fact]
    public async Task OverflowedResolvedList_UsesOnlyTheCountCommand()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&asOf=2026-06-15T12:00:00Z&page={int.MaxValue}&pageSize=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, response.GetProperty("items").GetArrayLength());
        Assert.Single(commands.Commands);
        Assert.Contains("count", commands.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonPublishedResolvedList_UsesNoContentCommands()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&status=Draft&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, response.GetProperty("items").GetArrayLength());
        Assert.Empty(commands.Commands);
    }

    [Fact]
    public async Task TagFilteredResolvedList_UsesAParameterizedArrayPredicate()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&tags=%20Featured%20&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, commands.Commands.Count);
        Assert.All(commands.Commands, command => Assert.Contains("tags @> @", command, StringComparison.Ordinal));
        Assert.DoesNotContain("featured", string.Join(' ', commands.Commands), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFilteredResolvedList_UsesAParameterizedPatternAndTwoCommands()
    {
        var commands = new ContentCommandRecorder();
        await using var factory = CreateFactory(commands);
        using var client = factory.CreateClient();
        var workspaceId = await SeedTwoResolvedItemsAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        commands.Start();

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspaces/{workspaceId}/content?resolve=true&q=first_value&asOf=2026-06-15T12:00:00Z",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, commands.Commands.Count);
        Assert.All(commands.Commands, command => Assert.Contains(" ilike ", command, StringComparison.Ordinal));
        Assert.DoesNotContain("first_value", string.Join(' ', commands.Commands), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(ContentCommandRecorder commands) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddDbContext<CmsifyDbContext>((_, options) => options.AddInterceptors(commands));
            }));

    private static async Task<Guid> SeedTwoResolvedItemsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        var workspace = new Workspace { Name = "Resolved query", Slug = $"resolved-{Guid.NewGuid():N}" };
        var template = new Template { WorkspaceId = workspace.Id, Name = "Article", Slug = $"article-{Guid.NewGuid():N}" };
        var templateVersion = new TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = TemplateVersionStatus.Published
        };
        template.Versions.Add(templateVersion);
        var first = PublishedItem(workspace.Id, templateVersion.Id, "first_value", DateTimeOffset.Parse("2026-06-01T00:00:00Z"), ["featured", "news"]);
        var second = PublishedItem(workspace.Id, templateVersion.Id, "firstXvalue", DateTimeOffset.Parse("2026-06-02T00:00:00Z"));

        dbContext.Workspaces.Add(workspace);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = templateVersion.Id;
        dbContext.ApiClients.Add(new ApiClient
        {
            Name = $"Resolved Query {Guid.NewGuid():N}",
            TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
            Role = UserRole.Reader,
            WorkspaceId = workspace.Id,
            CreatedByUserId = adminUserId
        });
        dbContext.ContentItems.AddRange(first.Item, second.Item);
        dbContext.ContentVersions.AddRange(first.Version, second.Version);
        await dbContext.SaveChangesAsync();
        return workspace.Id;
    }

    private static (ContentItem Item, ContentVersion Version) PublishedItem(
        Guid workspaceId,
        Guid templateVersionId,
        string slug,
        DateTimeOffset publishedAt,
        IList<string>? tags = null)
    {
        var item = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = templateVersionId,
            Status = ContentStatus.Published,
            Slug = slug,
            PublishedAt = publishedAt
        };
        var version = new ContentVersion
        {
            ContentItemId = item.Id,
            WorkspaceId = workspaceId,
            VersionNumber = 1,
            Status = ContentVersionStatus.Published,
            TemplateVersionId = templateVersionId,
            Slug = slug,
            Tags = tags ?? [],
            PublishedAt = publishedAt
        };
        return (item, version);
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed class ContentCommandRecorder : DbCommandInterceptor
    {
        private readonly Lock sync = new();
        private bool isStarted;

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (sync)
                {
                    return commands.ToList();
                }
            }
        }

        private readonly List<string> commands = [];

        public void Start()
        {
            lock (sync)
            {
                commands.Clear();
                isStarted = true;
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(string commandText)
        {
            var normalized = Regex.Replace(commandText, @"\s+", " ").Trim().ToLowerInvariant();
            if (!normalized.Contains("content_versions", StringComparison.Ordinal)
                && !normalized.Contains("template_versions", StringComparison.Ordinal))
            {
                return;
            }

            lock (sync)
            {
                if (isStarted)
                {
                    commands.Add(normalized);
                }
            }
        }
    }
}
